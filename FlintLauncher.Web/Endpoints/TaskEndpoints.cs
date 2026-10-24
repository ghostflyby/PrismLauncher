// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Task endpoints: list/get/abort/proceed/log/SSE-stream over the in-memory FlintTaskRegistry.

using System.Text.Json;
using FlintLauncher.Web.Models;
using FlintLauncher.Web.OpenApi;
using FlintLauncher.Web.Services;

namespace FlintLauncher.Web.Endpoints;

public static class TaskEndpoints
{
    public static void MapTasks(this RouteGroupBuilder api)
    {
        api.WithTags("Tasks");
        api.MapGet("/tasks", (FlintTaskRegistry registry) =>
                Results.Ok(registry.All().Select(registry.View).ToArray()))
            .Produces<TaskView[]>();

        api.MapGet("/tasks/{id}", (string id, FlintTaskRegistry registry) =>
        {
            var entry = registry.Get(id);
            return entry is null
                ? ApiJson.NotFound("task_not_found", $"no task '{id}'")
                : Results.Ok(registry.View(entry));
        }).Produces<TaskView>();

        api.MapPost("/tasks/{id}/abort", (string id, FlintTaskRegistry registry) =>
        {
            var entry = registry.Get(id);
            if (entry is null)
            {
                return ApiJson.NotFound("task_not_found", $"no task '{id}'");
            }

            if (registry.IsTerminal(entry))
            {
                return ApiJson.Conflict("task_finished", $"task '{id}' already finished");
            }

            registry.Abort(entry);
            return Results.NoContent();
        }).Produces(204);

        api.MapPost("/tasks/{id}/proceed", (string id, FlintTaskRegistry registry) =>
        {
            var entry = registry.Get(id);
            if (entry is null)
            {
                return ApiJson.NotFound("task_not_found", $"no task '{id}'");
            }

            // launch tasks are registered with AutoProceed=false: the client drives the
            // ReadyForLaunch gate by POSTing here once the task reports state "waiting"
            if (entry.Controller is null)
            {
                return ApiJson.Conflict("not_a_launch_task", $"task '{id}' has no proceed gate");
            }

            if (entry.Controller.Launcher is null)
            {
                return ApiJson.Conflict("launch_not_ready", "the launch task has not armed a launcher yet");
            }

            // a premature or duplicate POST must be observable: LaunchTask.Proceed() no-ops
            // outside the Waiting state (matching the C++), and a silently dropped proceed
            // leaves the game waiting on its stdin "launch" line forever
            if (!entry.Controller.Launcher.IsWaitingForProceed)
            {
                return ApiJson.Conflict("launch_not_waiting", $"task '{id}' is not waiting at a proceed gate");
            }

            registry.Proceed(entry);
            return Results.Ok(registry.View(entry));
        }).Produces<TaskView>();

        api.MapGet("/tasks/{id}/log", (string id, int? since, FlintTaskRegistry registry) =>
        {
            var entry = registry.Get(id);
            if (entry is null)
            {
                return ApiJson.NotFound("task_not_found", $"no task '{id}'");
            }

            var lines = registry.ReadLog(entry, since ?? 0);
            return Results.Ok(new TaskLogResponse(lines));
        }).Produces<TaskLogResponse>();

        // SSE: log lines as data events, the terminal state as a final "state" event.
        api.MapGet("/tasks/{id}/stream", async Task (string id, HttpContext ctx,
            FlintTaskRegistry registry,
            CancellationToken requestAborted) =>
        {
            var entry = registry.Get(id);
            if (entry is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(
                    JsonSerializer.Serialize(
                        new ApiErrorEnvelope(new ErrorResponse("task_not_found", $"no task '{id}'")),
                        FlintJsonContext.Default.ApiErrorEnvelope),
                    requestAborted);
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            var cursor = -1;
            var idlePolls = 0;
            try
            {
                while (!requestAborted.IsCancellationRequested)
                {
                    var lines = registry.ReadLog(entry, cursor);
                    foreach (var line in lines)
                    {
                        cursor = line.Index;
                        await ctx.Response.WriteAsync(
                            "data: " + JsonSerializer.Serialize(line, FlintJsonContext.Default.LogLineDto) + "\n\n",
                            requestAborted);
                    }

                    if (registry.IsTerminal(entry))
                    {
                        var view = registry.View(entry);
                        await ctx.Response.WriteAsync(
                            "event: state\ndata: " +
                            JsonSerializer.Serialize(view, FlintJsonContext.Default.TaskView) + "\n\n",
                            requestAborted);
                        await ctx.Response.Body.FlushAsync(requestAborted);
                        return;
                    }

                    if (lines.Count == 0)
                    {
                        // keep-alive comment roughly every 15s (50 x 300ms polls)
                        if (++idlePolls >= 50)
                        {
                            idlePolls = 0;
                            await ctx.Response.WriteAsync(": keep-alive\n\n", requestAborted);
                        }
                    }
                    else
                    {
                        idlePolls = 0;
                    }

                    await ctx.Response.Body.FlushAsync(requestAborted);
                    await Task.Delay(300, requestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected — the response is gone, nothing to write
            }
        }).WithMetadata(new SseMarker());
    }
}
