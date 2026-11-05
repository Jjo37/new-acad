import { describe, expect, it } from "vitest";
import { DRAWING_RUNTIME_DOMAIN_DEFINITION } from "../src/tools/domains/drawingRuntimeDomain.js";

describe("drawing runtime response contracts", () => {
  it("accepts a standalone drawing with no Civil 3D project", () => {
    const responseSchema = DRAWING_RUNTIME_DOMAIN_DEFINITION.actions.info.responseSchema;

    expect(responseSchema.safeParse({
      drawingName: "Drawing1.dwg",
      projectName: null,
      units: "feet",
    }).success).toBe(true);
  });
});
