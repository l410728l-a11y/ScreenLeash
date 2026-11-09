/**
 * ScreenLeash 桥 —— 业务层。
 *
 * 这一层不 import 任何天枢宿主 API，可以在普通 Node 里直接单测。
 * 数据契约来自 ScreenLeash（.NET）：
 *   - 数据根：%LOCALAPPDATA%\ScreenLeash\
 *   - 清单：items.json（Version=1，{ Version, Items[] }），原子写时保留 items.json.bak
 *   - 内容：blobs\（原文件副本）、thumbs\（png 缩略图），Item 里只存裸文件名
 *
 * 两个必须守住的边界：
 *   1) 只读。ItemStore 是卡片集合的唯一拥有者（内存持有列表 + File.Replace 落盘），
 *      外部直接改 items.json 会被它下一次保存整个覆盖。写入必须走挂件侧 receipt 通道。
 *   2) 裸文件名。blobName / thumbName 不得含路径分隔符（照搬 AppPaths.Resolve 规则），
 *      否则构造出的绝对路径会跑到数据目录之外。
 */
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { randomUUID } from 'node:crypto'

/** Kind 枚举（Item.cs）：0=Image, 1=Text, 2=File */
export const KIND_NAMES = { 0: 'image', 1: 'text', 2: 'file' }

/** EdgeKind 枚举（EdgeGeometry.cs）：0=Left, 1=Top, 2=Right, 3=Bottom */
export const EDGE_NAMES = { 0: 'left', 1: 'top', 2: 'right', 3: 'bottom' }

/** 列表里单条文本的预览上限（防止一张长文卡刷爆输出）。 */
export const MAX_PREVIEW = 120

/** items.json 的结构版本；读到更高版本视为不兼容（可能被新版写过）。 */
export const SCHEMA_VERSION = 1

/**
 * 默认数据根。Windows 上取 %LOCALAPPDATA%\ScreenLeash；
 * 非 Windows 或环境变量缺失时退回 <home>/AppData/Local/ScreenLeash（便于在测试里注入）。
 */
export function defaultLeashRoot(env = process.env, homeDir = os.homedir()) {
  const base = env.LOCALAPPDATA || path.join(homeDir, 'AppData', 'Local')
  return path.join(base, 'ScreenLeash')
}

/** 裸文件名 → 绝对路径；名字非法（含分隔符/卷号/上跳）时返回 null。 */
export function resolveUnder(dir, fileName) {
  if (typeof fileName !== 'string' || fileName.length === 0) return null
  if (/[\\/:]/.test(fileName) || fileName === '.' || fileName === '..') return null
  return path.join(dir, fileName)
}

function readJson(file) {
  try {
    const text = fs.readFileSync(file, 'utf8')
    if (typeof text !== 'string' || text.trim() === '') return { ok: false, reason: 'empty' }
    return { ok: true, doc: JSON.parse(text) }
  } catch (err) {
    return { ok: false, reason: err?.code || err?.name || 'read-error', message: String(err?.message ?? err) }
  }
}

/**
 * 读 cards 文档：items.json 失败（缺失/坏 JSON/版本越界）→ 回退 items.json.bak → 仍失败按空集。
 * 与 ItemStore.LoadCore 的降级顺序一致：绝不因为坏文件把工具调用变成抛错。
 */
export function readDocument(root) {
  const main = path.join(root, 'items.json')
  const backup = `${main}.bak`

  let first = readJson(main)
  let source = 'items.json'
  let recoveredFromBackup = false

  const acceptable = (r) => r.ok && r.doc && typeof r.doc === 'object' && Number(r.doc.Version) >= 1 && Number(r.doc.Version) <= SCHEMA_VERSION

  if (!acceptable(first)) {
    const second = readJson(backup)
    if (acceptable(second)) {
      first = second
      source = 'items.json.bak'
      recoveredFromBackup = true
    }
  }

  if (!acceptable(first)) {
    return {
      ok: false,
      root,
      items: [],
      source: null,
      recoveredFromBackup: false,
      reason: first.ok ? 'version-out-of-range' : first.reason,
      exists: fs.existsSync(main),
    }
  }

  const rawItems = Array.isArray(first.doc.Items) ? first.doc.Items : []
  const items = []
  let dropped = 0
  for (const raw of rawItems) {
    const normalized = normalizeItem(raw)
    if (normalized) items.push(normalized)
    else dropped += 1
  }

  return { ok: true, root, items, source, recoveredFromBackup, dropped, exists: true }
}

/**
 * 单条原始记录 → 归一化卡片；不满足 Kind 映射不变量时返回 null（丢弃而不是半残）。
 * 注意：raw.EffectiveLastAccessTicks / raw.HasBlob 是 C# 侧的只读派生属性，
 * 被 System.Text.Json 一起序列化了 —— 这里一律忽略，不作为原始数据使用。
 */
export function normalizeItem(raw) {
  if (!raw || typeof raw !== 'object') return null

  const kind = KIND_NAMES[raw.Kind]
  const edge = EDGE_NAMES[raw.Edge]
  if (!kind || !edge) return null

  const id = typeof raw.Id === 'string' ? raw.Id : null
  if (!id) return null

  const text = typeof raw.Text === 'string' ? raw.Text : ''
  const blobName = typeof raw.BlobName === 'string' && raw.BlobName ? raw.BlobName : null

  if (kind === 'text') {
    if (text === '') return null
    if (blobName) return null // Text 不落 blob
  } else if (!blobName) {
    return null // Image/File 必须有 blob
  }

  if (blobName && resolveUnder('blobs', blobName) === null) return null

  return {
    id,
    kind,
    edge,
    text,
    blobName,
    displayName: typeof raw.DisplayName === 'string' && raw.DisplayName ? raw.DisplayName : null,
    thumbName: typeof raw.ThumbName === 'string' && raw.ThumbName ? raw.ThumbName : null,
    byteSize: Number.isFinite(Number(raw.ByteSize)) ? Number(raw.ByteSize) : 0,
    createdAt: typeof raw.CreatedAt === 'string' ? raw.CreatedAt : null,
    order: Number.isFinite(Number(raw.Order)) ? Number(raw.Order) : 0,
  }
}

function previewOf(item) {
  if (item.kind === 'text') {
    const flat = item.text.replace(/\s+/g, ' ').trim()
    return flat.length > MAX_PREVIEW ? `${flat.slice(0, MAX_PREVIEW)}…` : flat
  }
  return item.displayName || item.blobName || '(未命名)'
}

function describe(item) {
  return {
    id: item.id,
    shortId: item.id.slice(0, 8),
    kind: item.kind,
    edge: item.edge,
    order: item.order,
    byteSize: item.byteSize,
    createdAt: item.createdAt,
    preview: previewOf(item),
  }
}

/** leash_info 的实际内容：不打游戏，只报事实。 */
export function infoLeash(root = defaultLeashRoot()) {
  const doc = readDocument(root)
  const itemsFile = path.join(root, 'items.json')
  let lastWrite = null
  let bytes = 0
  try {
    const st = fs.statSync(itemsFile)
    lastWrite = st.mtime.toISOString()
    bytes = st.size
  } catch {
    /* 文件不存在：保持 null */
  }

  const byEdge = {}
  let blobBytes = 0
  for (const item of doc.items) {
    byEdge[item.edge] = (byEdge[item.edge] ?? 0) + 1
    if (item.blobName) blobBytes += Math.max(0, item.byteSize)
  }

  return {
    dataRoot: root,
    dataRootExists: fs.existsSync(root),
    document: {
      state: doc.ok ? 'ok' : 'unavailable',
      source: doc.source,
      recoveredFromBackup: doc.recoveredFromBackup,
      reason: doc.ok ? undefined : doc.reason,
      dropped: doc.dropped ?? 0,
    },
    itemsFile: {
      path: itemsFile,
      exists: doc.exists,
      bytes,
      lastWrite,
    },
    counts: {
      total: doc.items.length,
      byEdge,
      blobBytes,
    },
    note: '只读视图。ScreenLeash 是卡片集合的唯一拥有者，本插件不会写 items.json。',
  }
}

/**
 * leash_list：列表。可按边/类型筛选；limit 默认 50。
 * 排序与 ScreenLeash 一致：(Edge, Order, Id)。
 */
export function listLeash(root = defaultLeashRoot(), args = {}) {
  const { edge, kind, limit } = args ?? {}
  const doc = readDocument(root)

  if (!doc.ok && doc.reason !== 'version-out-of-range' && !doc.exists) {
    return { ok: false, reason: 'not-installed', root, items: [], total: 0 }
  }

  let items = doc.items.slice()
  if (edge !== undefined && edge !== null && edge !== '') {
    const want = String(edge).toLowerCase()
    items = items.filter((i) => i.edge === want)
  }
  if (kind !== undefined && kind !== null && kind !== '') {
    const want = String(kind).toLowerCase()
    items = items.filter((i) => i.kind === want)
  }

  const edgeRank = { left: 0, top: 1, right: 2, bottom: 3 }
  items.sort(
    (a, b) =>
      (edgeRank[a.edge] ?? 9) - (edgeRank[b.edge] ?? 9) ||
      a.order - b.order ||
      (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  )

  const total = items.length
  const cap = Number.isFinite(Number(limit)) && Number(limit) > 0 ? Number(limit) : 50
  const shown = items.slice(0, cap)

  return {
    ok: true,
    root,
    total,
    returned: shown.length,
    truncated: total > shown.length,
    recoveredFromBackup: doc.recoveredFromBackup,
    items: shown.map(describe),
  }
}

/**
 * leash_read：按完整 Id 或 Id 前缀（≥4 位）取单张卡的完整内容。
 * 图片/文件卡返回 blob 绝对路径，由调用方决定要不要读字节（插件不搬大文件）。
 */
export function readLeashItem(root = defaultLeashRoot(), args = {}) {
  const id = typeof args?.id === 'string' ? args.id.trim() : ''
  if (!id) return { ok: false, reason: 'id-required' }
  if (id.length < 4) return { ok: false, reason: 'id-too-short' }

  const doc = readDocument(root)
  if (!doc.ok) return { ok: false, reason: doc.reason ?? 'unavailable', root }

  const lowered = id.toLowerCase()
  const matches = doc.items.filter(
    (i) => i.id.toLowerCase() === lowered || i.id.toLowerCase().startsWith(lowered)
  )

  if (matches.length === 0) return { ok: false, reason: 'not-found', id, total: doc.items.length }
  if (matches.length > 1) {
    return {
      ok: false,
      reason: 'ambiguous',
      id,
      candidates: matches.map((i) => ({ shortId: i.id.slice(0, 8), kind: i.kind, preview: previewOf(i) })),
    }
  }

  const item = matches[0]
  const out = {
    ok: true,
    root,
    id: item.id,
    kind: item.kind,
    edge: item.edge,
    order: item.order,
    byteSize: item.byteSize,
    createdAt: item.createdAt,
    displayName: item.displayName,
  }

  if (item.kind === 'text') {
    out.text = item.text
    out.textLength = item.text.length
  } else {
    out.blobName = item.blobName
    const blobPath = resolveUnder(path.join(root, 'blobs'), item.blobName)
    out.blobPath = blobPath && fs.existsSync(blobPath) ? blobPath : null
    out.blobMissing = out.blobPath === null
    if (item.thumbName) {
      const thumbPath = resolveUnder(path.join(root, 'thumbs'), item.thumbName)
      out.thumbPath = thumbPath && fs.existsSync(thumbPath) ? thumbPath : null
    }
  }

  return out
}

/** 单条文本注入的上限（挂件侧还有自己的 MaxTextLength，这里先挡）。 */
export const MAX_PUT_TEXT = 20000

/**
 * leash_put：把一条内容投递到 ScreenLeash 的收件箱。
 *
 * 插件**不碰** items.json：只写 `inbox\<name>.json.tmp` 再 rename 成 `.json`（同卷 rename 是原子的，
 * 挂件不会读到半个文件），由挂件侧轮询落卡。
 *
 * 数据目录不存在时直接拒绝——不能因为一次 tool call 就在用户机器上凭空造一个 ScreenLeash 目录。
 */
export function putLeashItem(root = defaultLeashRoot(), args = {}) {
  const a = args ?? {}
  const kind =
    typeof a.kind === 'string' && a.kind
      ? a.kind.toLowerCase()
      : typeof a.text === 'string' && a.text
        ? 'text'
        : typeof a.path === 'string' && a.path
          ? 'file'
          : ''
  const edge = typeof a.edge === 'string' && a.edge ? a.edge.toLowerCase() : 'right'

  if (kind !== 'text' && kind !== 'file') {
    return { ok: false, reason: 'kind-invalid', hint: 'kind 只能是 text 或 file（或只给 text / path 让它自行推断）' }
  }
  if (!Object.values(EDGE_NAMES).includes(edge)) {
    return { ok: false, reason: 'edge-invalid', allowed: Object.values(EDGE_NAMES) }
  }

  let rootIsDir = false
  try {
    rootIsDir = fs.statSync(root).isDirectory()
  } catch {
    rootIsDir = false
  }
  if (!rootIsDir) {
    return { ok: false, reason: 'not-installed', root, hint: 'ScreenLeash 数据目录不存在，请先运行一次挂件' }
  }

  const item = { kind, edge }
  if (kind === 'text') {
    const text = typeof a.text === 'string' ? a.text : ''
    if (text.trim() === '') return { ok: false, reason: 'text-required' }
    if (text.length > MAX_PUT_TEXT) return { ok: false, reason: 'text-too-long', limit: MAX_PUT_TEXT, actual: text.length }
    item.text = text
  } else {
    const source = typeof a.path === 'string' ? a.path : ''
    if (source.trim() === '') return { ok: false, reason: 'path-required' }
    let stat = null
    try {
      stat = fs.statSync(source)
    } catch {
      stat = null
    }
    if (!stat || !stat.isFile()) return { ok: false, reason: 'path-not-a-file', path: source }
    item.path = source
  }

  const submitted = submitToInbox(root, { items: [item] })
  if (!submitted.ok) return submitted

  return {
    ...submitted,
    item,
    note: '已投递到收件箱；挂件进程轮询（约 1.5 秒）后落卡。插件没有改 items.json。',
  }
}

/**
 * 把一条命令投递到收件箱：写 `<name>.json.tmp` 再 rename 成 `.json`。
 * 挂件只认 `.json`，且要求文件安静 2 秒后才处理 —— 所以必须先 tmp 后 rename。
 */
export function submitToInbox(root, payload) {
  let rootIsDir = false
  try {
    rootIsDir = fs.statSync(root).isDirectory()
  } catch {
    rootIsDir = false
  }
  if (!rootIsDir) {
    return { ok: false, reason: 'not-installed', root, hint: 'ScreenLeash 数据目录不存在，请先运行一次挂件' }
  }

  const inbox = path.join(root, 'inbox')
  const stamp = new Date().toISOString().replace(/[-:.]/g, '').slice(0, 15)
  const name = `tianshu-${stamp}-${randomUUID().slice(0, 8)}`
  const tmp = path.join(inbox, `${name}.json.tmp`)
  const final = path.join(inbox, `${name}.json`)

  try {
    fs.mkdirSync(inbox, { recursive: true })
    fs.writeFileSync(tmp, JSON.stringify({ source: 'tianshu', ...payload }, null, 2), 'utf8')
    fs.renameSync(tmp, final)
  } catch (err) {
    try {
      fs.rmSync(tmp, { force: true })
    } catch {
      /* 清理失败不影响结论 */
    }
    return { ok: false, reason: 'write-failed', message: String(err?.message ?? err) }
  }

  return { ok: true, file: final }
}

/**
 * 把 Id 前缀展开成完整 Guid。
 * 落盘端只认完整 Guid（防手抖前缀删错卡），所以前缀匹配这件事只在插件侧做。
 */
export function resolveCardId(root, rawId) {
  const id = typeof rawId === 'string' ? rawId.trim() : ''
  if (!id) return { ok: false, reason: 'id-required' }
  if (id.length < 4) return { ok: false, reason: 'id-too-short' }

  const doc = readDocument(root)
  if (!doc.ok) return { ok: false, reason: doc.reason ?? 'unavailable', root }

  const lowered = id.toLowerCase()
  const matches = doc.items.filter(
    (i) => i.id.toLowerCase() === lowered || i.id.toLowerCase().startsWith(lowered)
  )

  if (matches.length === 0) return { ok: false, reason: 'not-found', id, total: doc.items.length }
  if (matches.length > 1) {
    return {
      ok: false,
      reason: 'ambiguous',
      id,
      candidates: matches.map((i) => ({ shortId: i.id.slice(0, 8), kind: i.kind, preview: previewOf(i) })),
    }
  }

  return { ok: true, id: matches[0].id }
}

function normalizeEdge(value, fallback) {
  if (value === undefined || value === null || value === '') {
    return { ok: true, edge: fallback }
  }

  const edge = String(value).toLowerCase()
  if (!Object.values(EDGE_NAMES).includes(edge)) {
    return { ok: false, reason: 'edge-invalid', allowed: Object.values(EDGE_NAMES) }
  }

  return { ok: true, edge }
}

/** leash_remove：删掉一张卡。id 支持 ≥4 位前缀，插件侧展开成完整 Guid 再投递。 */
export function removeLeashItem(root = defaultLeashRoot(), args = {}) {
  const resolved = resolveCardId(root, args?.id)
  if (!resolved.ok) return resolved

  const submitted = submitToInbox(root, { action: 'remove', id: resolved.id })
  if (!submitted.ok) return submitted

  return { ...submitted, id: resolved.id, note: '已投递删除命令；挂件轮询（约 1.5 秒）后执行。' }
}

/** leash_move：把卡片换到另一条边。目标边必须显式给——猜错就会悄悄挪走。 */
export function moveLeashItem(root = defaultLeashRoot(), args = {}) {
  const resolved = resolveCardId(root, args?.id)
  if (!resolved.ok) return resolved

  const edge = normalizeEdge(args?.edge, null)
  if (!edge.ok) return edge
  if (!edge.edge) return { ok: false, reason: 'edge-required', allowed: Object.values(EDGE_NAMES) }

  const submitted = submitToInbox(root, { action: 'move', id: resolved.id, edge: edge.edge })
  if (!submitted.ok) return submitted

  return { ...submitted, id: resolved.id, edge: edge.edge, note: '已投递移动命令；挂件轮询后执行。' }
}

/**
 * leash_paste：让挂件把系统剪贴板当前内容挂到绳子上。
 *
 * 插件**自己不读剪贴板**——那要 shell 权限 + STA 线程，而且行为会和用户手点菜单不一致。
 * 这里只投一条命令，由挂件用它现成的 PasteFromClipboardAsync 执行。
 */
export function pasteFromClipboard(root = defaultLeashRoot(), args = {}) {
  const edge = normalizeEdge(args?.edge, 'right')
  if (!edge.ok) return edge

  const submitted = submitToInbox(root, { action: 'paste', edge: edge.edge })
  if (!submitted.ok) return submitted

  return {
    ...submitted,
    edge: edge.edge,
    note: '已投递粘贴命令；挂件会用它自己的剪贴板逻辑挂上（约 1.5 秒）。',
  }
}

function ok(content) {
  return { content: JSON.stringify(content, null, 2) }
}

function fail(content) {
  return { content: JSON.stringify(content, null, 2), isError: true }
}

/**
 * 工具集。dataRoot 可注入，测试时指向临时目录。
 */
export function createTools(dataRoot) {
  const root = () => dataRoot ?? defaultLeashRoot()

  return [
    {
      definition: {
        name: 'leash_info',
        description:
          '查看 ScreenLeash（屏幕边缘挂件）的数据目录状态：卡片总数、各边分布、items.json 最后写入时间。只读。',
        input_schema: { type: 'object', properties: {}, additionalProperties: false },
      },
      execute(_args, call) {
        const target = root()
        return ok(infoLeash(target))
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => true,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_list',
        description:
          '列出 ScreenLeash 绳子上挂着的卡片（文本预览 / 图片文件名 / 文件文件名），可按边或类型筛选。只读。',
        input_schema: {
          type: 'object',
          properties: {
            edge: { type: 'string', enum: [...Object.values(EDGE_NAMES)] },
            kind: { type: 'string', enum: [...Object.values(KIND_NAMES)] },
            limit: { type: 'number' },
          },
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        return ok(listLeash(target, call?.args ?? {}))
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => true,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_read',
        description:
          '读 ScreenLeash 单张卡片的完整内容：文本卡返回原文；图片/文件卡返回 blob 绝对路径（再用 read_file 读字节）。只读。',
        input_schema: {
          type: 'object',
          properties: {
            id: { type: 'string', description: '卡片 Id，支持 ≥4 位前缀' },
          },
          required: ['id'],
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        const result = readLeashItem(target, call?.args ?? {})
        return result.ok ? ok(result) : fail(result)
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => true,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_put',
        description:
          '把一段文本或一个本地文件挂到 ScreenLeash 绳子上。写入其收件箱（不碰 items.json），由挂件进程落卡。一次一条。',
        input_schema: {
          type: 'object',
          properties: {
            kind: { type: 'string', enum: ['text', 'file'], description: '缺省时按 text / path 自行推断' },
            text: { type: 'string', description: 'kind=text 时的内容' },
            path: { type: 'string', description: 'kind=file 时的本地文件绝对路径' },
            edge: { type: 'string', enum: [...Object.values(EDGE_NAMES)], description: '挂到哪条边，缺省 right' },
          },
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        const result = putLeashItem(target, call?.args ?? {})
        return result.ok ? ok(result) : fail(result)
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => false,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_remove',
        description:
          '删掉 ScreenLeash 绳子上的一张卡片。id 支持 ≥4 位前缀（插件展开成完整 Id）。删除不可恢复，会请求用户确认。',
        input_schema: {
          type: 'object',
          properties: {
            id: { type: 'string', description: '卡片 Id 或 ≥4 位前缀' },
          },
          required: ['id'],
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        const result = removeLeashItem(target, call?.args ?? {})
        return result.ok ? ok(result) : fail(result)
      },
      // 删卡片是破坏性且不可恢复的：必须走宿主审批。
      requiresApproval: () => true,
      isConcurrencySafe: () => false,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_move',
        description: '把 ScreenLeash 绳子上的一张卡片移到另一条边（left / top / right / bottom）。',
        input_schema: {
          type: 'object',
          properties: {
            id: { type: 'string', description: '卡片 Id 或 ≥4 位前缀' },
            edge: { type: 'string', enum: [...Object.values(EDGE_NAMES)], description: '目标边，必填' },
          },
          required: ['id', 'edge'],
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        const result = moveLeashItem(target, call?.args ?? {})
        return result.ok ? ok(result) : fail(result)
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => false,
      isEnabled: () => true,
    },
    {
      definition: {
        name: 'leash_paste',
        description:
          '让 ScreenLeash 把系统剪贴板当前内容挂到绳子上（插件不读剪贴板，命令由挂件执行）。' +
          '适用于用户刚复制了图片或文字、想让它出现在绳子上的场景。',
        input_schema: {
          type: 'object',
          properties: {
            edge: { type: 'string', enum: [...Object.values(EDGE_NAMES)], description: '挂到哪条边，缺省 right' },
          },
          additionalProperties: false,
        },
      },
      execute(_args, call) {
        const target = root()
        const result = pasteFromClipboard(target, call?.args ?? {})
        return result.ok ? ok(result) : fail(result)
      },
      requiresApproval: () => false,
      isConcurrencySafe: () => false,
      isEnabled: () => true,
    },
  ]
}

/** 工具名清单：清单文件、入口适配层、业务层三处必须一致（宿主准入条件）。 */
export const TOOL_NAMES = [
  'leash_info',
  'leash_list',
  'leash_read',
  'leash_put',
  'leash_remove',
  'leash_move',
  'leash_paste',
]

export const tools = createTools()
