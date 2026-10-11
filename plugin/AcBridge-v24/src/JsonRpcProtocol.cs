using System.Text.Json;
using System.Text.Json.Nodes;

namespace Civil3DMcpPlugin;

internal static class JsonRpcProtocol
{
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
  };
}
