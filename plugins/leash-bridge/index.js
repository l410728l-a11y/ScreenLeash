/**
 * 宿主适配层 —— 只做「把 impl.js 接到天枢上」，不写业务逻辑。
 */
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { defineTianshuPlugin } from './tpi.js'
import { createTools } from './impl.js'

/**
 * 运行时描述一律以清单为准。
 *
 * 审核实测过一个坑：清单里写简版描述、代码里写详版，宿主看到的两份文案不一致。
 * 这里直接读清单覆盖，从物理上就不可能再分叉。
 */
const manifest = JSON.parse(
  readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'tianshu.plugin.json'), 'utf8')
)
const MANIFEST_DESCRIPTIONS = Object.fromEntries(
  manifest.contributes.tools.map((t) => [t.name, t.description])
)

/**
 * 顶层路径参数声明表。
 *
 * 宿主据此做内核路径守卫，并把参数换成规范化绝对路径。只有**顶层** input_schema.properties 里的键
 * 会被这里命中——嵌套在数组里的 path 拿不到守卫（这也是 leash_put 只收单条的原因）。
 */
const PATH_KEYS = {
  path: 'read',
  file_path: 'read',
  dir_path: 'read',
  output_path: 'write',
  destination_path: 'write',
}

function pathParamsFor(tool) {
  const props = tool.definition.input_schema?.properties ?? {}
  const out = {}
  for (const key of Object.keys(props)) {
    if (key in PATH_KEYS) out[key] = PATH_KEYS[key]
  }
  return out
}

/** 把 impl.js 的工具对象适配成 TPI 的 TpiToolDefinition。 */
function toTpiTool(tool) {
  const pathParams = pathParamsFor(tool)
  return {
    name: tool.definition.name,
    // 描述以清单为唯一来源，避免运行时与清单两份文案不一致
    description: MANIFEST_DESCRIPTIONS[tool.definition.name] ?? tool.definition.description,
    inputSchema: tool.definition.input_schema,
    concurrencySafe: tool.isConcurrencySafe?.() === true,
    requiresApproval: tool.requiresApproval?.() === true,
    // 宿主支持 timeoutMs（tpi.d.ts:24）；不声明就没有显式预算。
    ...(typeof tool.timeoutMs === 'number' ? { timeoutMs: tool.timeoutMs } : {}),
    // 只有真的吃路径参数的工具才带这个字段：乱声明会让宿主对普通字符串做路径规范化。
    ...(Object.keys(pathParams).length > 0 ? { pathParams } : {}),
    // 透传完整 call：impl 层要靠它拿 cwd / sessionId
    execute: call => tool.execute(call.args, call),
  }
}

export default defineTianshuPlugin({
  id: 'leash-bridge',
  activate(host) {
    host.log?.debug?.(`leash-bridge 已激活 · TPI ${host.tpiVersion ?? '未知'}`)
    return {
      tools: createTools().map(toTpiTool),
    }
  },
})
