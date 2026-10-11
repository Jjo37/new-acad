import { z } from "zod";

/**
 * Common response schemas shared across domains.
 *
 * These are intentionally narrow at the top level and liberal on unknown
 * fields (`.passthrough()`), so they document the minimum contract every
 * mutating tool satisfies without rejecting legitimate plugin-side additions.
 *
 * When tightening domain-specific responses, prefer extending one of these
 * rather than re-declaring `.passthrough()` schemas.
 */

/**
 * Envelope for any mutation that returns a reference to the object it touched.
 * Every create/update/delete operation in the plugin should at minimum return
 * either a handle, an ObjectId-style string, or a name the caller can use to
 * look the object up again.
 */
export const MutationResponseSchema = z.object({
  name: z.string().optional(),
  handle: z.string().optional(),
  objectId: z.string().optional(),
}).passthrough();

/**
 * Envelope for operations that kick off a background job on the plugin side.
 * The plugin returns at minimum `{ jobId }` and may include state/message.
 * Callers poll civil3d_job(status, jobId) for progress and the final result.
 */
export const JobLaunchResponseSchema = z.object({
  jobId: z.string(),
  state: z.enum(["running", "completed", "failed", "cancelled"]).optional(),
  message: z.string().optional(),
}).passthrough();

/**
 * Envelope for delete operations: the plugin signals success and optionally
 * echoes the deleted object's handle/name.
 */
export const DeleteResponseSchema = z.object({
  deleted: z.boolean().optional(),
  name: z.string().optional(),
  handle: z.string().optional(),
}).passthrough();

/**
 * Envelope for "report" style operations that produce a human-readable text
 * block plus optional structured data.
 */
export const ReportResponseSchema = z.object({
  report: z.string().optional(),
  format: z.string().optional(),
}).passthrough();

export type MutationResponse = z.infer<typeof MutationResponseSchema>;
export type JobLaunchResponse = z.infer<typeof JobLaunchResponseSchema>;
export type DeleteResponse = z.infer<typeof DeleteResponseSchema>;
export type ReportResponse = z.infer<typeof ReportResponseSchema>;
