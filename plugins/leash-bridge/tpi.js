/**
 * TPI SDK 探针（随插件分发，不打包进任何依赖）。
 *
 * 天枢宿主在加载插件入口之前，会先在进程里安装只读的 globalThis.__tianshuTPI。
 * 插件入口通过它拿到 defineTianshuPlugin —— 所以 TPI 插件无法脱离宿主单独运行，
 * 在普通 Node 里 import 本文件会直接抛错，这是设计如此，不是 bug。
 */
export function defineTianshuPlugin(spec) {
  const sdk = globalThis.__tianshuTPI
  if (!sdk || typeof sdk.defineTianshuPlugin !== 'function') {
    throw new Error('不在天枢宿主内：TPI 插件只能由天枢加载，不能脱离宿主运行。')
  }
  return sdk.defineTianshuPlugin(spec)
}
