// CommandControlCommands.cs -- getCommandState / cancelCommand (2026-11-12, v1.16.0)
//
// Why these exist: on 2026-11-12 an agent sent MERGE through executeCommand, the command sat
// waiting for input, every later call queued behind it, and the only way out was a human
// pressing ESC in AutoCAD (the session then died). The AI had no way to ask "is the editor
// idle, running something, or parked on a prompt/dialog?" and no way to break out.
//
// Scope: both methods are diagnostic/abort only. getCommandState reads system variables and
// the host watchdog snapshot; cancelCommand posts ESC ESC to the command queue plus ESC to a
// blocking dialog (the same best-effort interrupt the watchdog already uses). Neither touches
// the transaction system, so neither can modify drawing data.
//
// getCommandState -> {
//   state: idle | running | transparent | script | lisp | dialog,
//   command, cmdActive (raw bitmask), acceptingCommands,
//   hostBusy, hostBusySinceUnixMs, activeDocument, openDocuments,
//   blockingDialog, lastTimeout
// }
// cancelCommand -> { before, after, note } -- before/after are getCommandState payloads.

using System.Text.Json.Nodes;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

internal static class CommandControlCommands
{
  public static Task<object?> GetCommandStateAsync(JsonObject? parameters)
  {
    return Task.FromResult<object?>(CivilExecution.GetCommandContextState(true));
  }

  public static Task<object?> CancelCommandAsync(JsonObject? parameters)
  {
    // No parameters on purpose: the only thing this may cancel is whatever the editor is
    // waiting on. Cancelling "the previous request" would need per-request bookkeeping that
    // the host does not expose, and would be a different (riskier) feature.
    var before = CivilExecution.GetCommandContextState(false);
    CivilExecution.InterruptActiveCommand();
    var after = CivilExecution.GetCommandContextState(false);

    return Task.FromResult<object?>(new Dictionary<string, object?>
    {
      ["before"] = before,
      ["after"] = after,
      ["sent"] = "ESC ESC to the command queue, plus ESC to a blocking dialog when one was found",
      ["note"] = "best-effort: ESC is posted asynchronously; re-read getCommandState after a moment. "
        + "ESC cannot cancel a modal dialog owned by another process.",
    });
  }
}
