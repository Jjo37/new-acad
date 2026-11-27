type LogLevel = "debug" | "info" | "warn" | "error";

const LOG_LEVELS: Record<LogLevel, number> = {
  debug: 0,
  info: 1,
  warn: 2,
  error: 3,
};

// 2026-10-15: 非法级别会让 LOG_LEVELS[currentLevel] 为 undefined → `x >= undefined` 恒 false → 所有日志（含 error）被吞。
const rawLevel = (process.env.CIVIL3D_LOG_LEVEL ?? "info").toLowerCase();
const currentLevel: LogLevel = (rawLevel in LOG_LEVELS ? rawLevel : "info") as LogLevel;
if (rawLevel !== currentLevel) {
  console.warn(
    `[logger] unknown CIVIL3D_LOG_LEVEL='${process.env.CIVIL3D_LOG_LEVEL}' -> falling back to 'info' (valid: debug|info|warn|error)`,
  );
}

function shouldLog(level: LogLevel): boolean {
  return LOG_LEVELS[level] >= LOG_LEVELS[currentLevel];
}

function formatMessage(level: LogLevel, component: string, message: string, data?: Record<string, unknown>): string {
  const timestamp = new Date().toISOString();
  const base = `[${timestamp}] [${level.toUpperCase()}] [${component}] ${message}`;
  if (data && Object.keys(data).length > 0) {
    return `${base} ${JSON.stringify(data)}`;
  }
  return base;
}

export function createLogger(component: string) {
  return {
    debug(message: string, data?: Record<string, unknown>) {
      if (shouldLog("debug")) {
        console.error(formatMessage("debug", component, message, data));
      }
    },
    info(message: string, data?: Record<string, unknown>) {
      if (shouldLog("info")) {
        console.error(formatMessage("info", component, message, data));
      }
    },
    warn(message: string, data?: Record<string, unknown>) {
      if (shouldLog("warn")) {
        console.error(formatMessage("warn", component, message, data));
      }
    },
    error(message: string, data?: Record<string, unknown>) {
      if (shouldLog("error")) {
        console.error(formatMessage("error", component, message, data));
      }
    },
  };
}
