using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class JsonRpcProtocol
{
  /// <summary>用与线路完全相同的序列化选项估算字节数（响应预算判断用）。</summary>
  internal static long EstimateBytes(object? value)
  {
    try { return JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions).LongLength; }
    catch (System.Exception) { return 0; }
  }

  public static string SerializeResult(JsonNode? id, object? result)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["result"] = result == null ? null : JsonSerializer.SerializeToNode(result, SerializerOptions),
    };

    return response.ToJsonString(SerializerOptions);
  }

  public static string SerializeError(JsonNode? id, int numericCode, string domainCode, string message)
  {
    var response = new JsonObject
    {
      ["jsonrpc"] = "2.0",
      ["id"] = id,
      ["error"] = new JsonObject
      {
        ["code"] = numericCode,
        ["message"] = message,
        ["data"] = new JsonObject
        {
          ["code"] = domainCode,
        },
      },
    };

    return response.ToJsonString(SerializerOptions);
  }

  public static int NumericErrorCode(string domainCode) => domainCode switch
  {
    "CIVIL3D.INVALID_JSON" => -32700,
    "CIVIL3D.INVALID_REQUEST" => -32600,
    "CIVIL3D.METHOD_NOT_FOUND" => -32601,
    "CIVIL3D.INVALID_INPUT" => -32602,
    // 2026-10-29: a capability gap is NOT an internal error. The JIT-gap path used to hardcode
    // -32603 (JSON-RPC "internal error", the same number a real crash carries) while
    // CivilSupport.Require() produced -32000 for the same domain code -> a client keying on the
    // numeric code could not tell "needs Civil 3D" from "the plugin blew up".
    "CIVIL3D.CIVIL_NOT_INSTALLED" => -32000,
    // 2026-10-29: make the table the single source even for the one code whose number IS the
    // JSON-RPC internal-error code. Without this arm the table said -32000 while the call site
    // hardcoded -32603 -- exactly the drift check-error-codes.js exists to catch.
    "CIVIL3D.INTERNAL_ERROR" => -32603,
    "CIVIL3D.NO_DRAWING" or "CIVIL3D.UNAVAILABLE" => -32001,
    "CIVIL3D.OBJECT_NOT_FOUND" => -32004,
    "CIVIL3D.TIMEOUT" => -32008,
    "CIVIL3D.CONFLICT" => -32009,
    "CIVIL3D.CANCELLED" => -32010,
    "CIVIL3D.HOST_BUSY" => -32029,
    "CIVIL3D.PATH_NOT_ALLOWED" => -32040,
    "CIVIL3D.FILE_TYPE_NOT_ALLOWED" => -32041,
    "CIVIL3D.FILE_IO_ERROR" => -32042,
    "CIVIL3D.AUTH_REQUIRED" => -32043,
    "CIVIL3D.FORBIDDEN" => -32044,
    "CIVIL3D.API_ERROR" => -32050,
    "CIVIL3D.TRANSACTION_FAILED" => -32051,
    "CIVIL3D.COMMAND_FAILED" => -32052,
    _ => -32000,
  };

  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
    // 2026-11-23 P34: 默认编码器把中文写成 \uXXXX（6 字节/字），中文响应白白膨胀 6 倍 ——
    //   listMethods 就因此撞上 64KB 摘要阈值，整个方法清单被换成摘要（AI 直接"看不见"方法）。
    //   走的是 UTF-8 线路（node/relay 都按 utf8 解码），原样输出中文是合法 JSON。
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };
}
