/**
 * 宿主适配层 —— 只做「把 impl.js 接到天枢上」，不写业务逻辑。
 */
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { defineTianshuPlugin } from './tpi.js'
import { createTools, inspectBody, buildBodyStatusBlock } from './impl.js'

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

    // 每会话最多注入一次：状态块进了对话历史后，后续轮次模型自己看得到；
    // 反复注入只会占 token、刷屏。
    let announced = false

    return {
      tools: createTools().map(toTpiTool),
      lifecycle: {
        /**
         * 每轮开始时判一次本体状态，只在「数据根不存在」时向本轮注入提示。
         *
         * 这个钩子每轮都会跑，两条硬纪律：
         *   1) 自己兜住所有异常 —— 绝不让 turnStart 把本轮对话搞坏；
         *   2) 宿主没给 promptContext（未声明能力或版本过旧）时直接返回。
         * 宿主给的 preTurn 预算 2000ms（rivet-runtime: budgetMs 0x7d0），实际是两次 stat。
         */
        turnStart() {
          if (announced) return
          const promptContext = host.promptContext
          if (!promptContext) return
          try {
            const block = buildBodyStatusBlock(inspectBody())
            if (block === null) return
            promptContext.setBlock(block)
            announced = true
          } catch (err) {
            host.log?.warn?.(`leash-bridge turnStart 失败（已忽略）：${String(err?.message ?? err)}`)
          }
        },
      },
    }
  },
})
