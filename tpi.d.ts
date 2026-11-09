/// <reference types="node" />

// TPI 1.0 author-facing signatures for the Tianshu 4.0.2 SDK.
export interface TpiToolResult {
  content: string;
  isError?: boolean;
}

export interface TpiToolCall {
  args: Record<string, unknown>;
  cwd: string;
  sessionId?: string;
  signal?: AbortSignal;
}

export interface TpiToolDefinition {
  name: string;
  description: string;
  inputSchema: Record<string, unknown>;
  concurrencySafe?: boolean;
  requiresApproval?: boolean;
  timeoutMs?: number;
  /** Every argument used as a path must declare its access mode here. */
  pathParams?: Record<string, 'read' | 'write'>;
  execute(call: TpiToolCall): Promise<TpiToolResult>;
}

export interface TpiStorage {
  dataDir(): string;
  openDatabase(fileName: string): import('node:sqlite').DatabaseSync;
}

export interface TpiAccount {
  userKey(): string;
}

export interface TpiEmbeddings {
  available(): boolean;
  modelId(): string;
  embed(texts: string[], signal?: AbortSignal): Promise<number[][]>;
}

export interface TpiSidePathLLM {
  available(): boolean;
  complete(prompt: string, options: { timeoutMs: number; signal?: AbortSignal }): Promise<string>;
}

export interface TpiPromptContext {
  /** During turnStart, set one XML block with a root tag prefixed by the plugin id. */
  setBlock(block: string | null): void;
}

export interface TpiTranscripts {
  listSessions(limit?: number): string[];
  read(sessionId: string): Array<{ role: 'user' | 'assistant'; content: string }>;
}

export interface TpiLog {
  debug(message: string): void;
  warn(message: string): void;
}

export interface TpiHost {
  readonly tpiVersion: string;
  readonly pluginId: string;
  readonly cwd: string;
  readonly sessionId?: string;
  readonly isWorker: boolean;
  readonly storage?: TpiStorage;
  readonly account?: TpiAccount;
  readonly embeddings?: TpiEmbeddings;
  readonly sidePathLLM?: TpiSidePathLLM;
  readonly promptContext?: TpiPromptContext;
  readonly transcripts?: TpiTranscripts;
  readonly log: TpiLog;
}

export interface TpiTurnStartEvent {
  userInput: string;
  turnMode: string;
  turn: number;
  signal?: AbortSignal;
}

export interface TpiTurnEndEvent {
  turn: number;
  userInput: string | null;
  assistantText: string;
  signal?: AbortSignal;
}

export interface TpiToolEndEvent {
  turn: number;
  tool: string;
  success: boolean;
  target?: string;
  resultPreview?: string;
}

export interface TpiSessionEndEvent {
  signal?: AbortSignal;
}

export interface TpiLifecycleHandlers {
  turnStart?(event: TpiTurnStartEvent): Promise<void> | void;
  turnEnd?(event: TpiTurnEndEvent): Promise<void> | void;
  toolEnd?(event: TpiToolEndEvent): Promise<void> | void;
  sessionEnd?(event: TpiSessionEndEvent): Promise<void> | void;
}

export interface TpiSessionContribution {
  tools?: TpiToolDefinition[];
  lifecycle?: TpiLifecycleHandlers;
  dispose?(): Promise<void> | void;
}

export interface TpiPluginSpec {
  id: string;
  activate(host: TpiHost): TpiSessionContribution | Promise<TpiSessionContribution>;
}

declare const tpiBrand: unique symbol;
export interface TpiPluginDefinition extends TpiPluginSpec {
  readonly [tpiBrand]: { tpi: string };
}

export function defineTianshuPlugin(spec: TpiPluginSpec): TpiPluginDefinition;
