// SPDX-License-Identifier: GPL-3.0-only
// One-line wrapper around the WebHostBuilder factory: integration tests host the exact same
// app in-process through FlintLauncher.Web.WebHostBuilder.BuildWebApplication(args).

using WebHostBuilder = FlintLauncher.Web.WebHostBuilder;

WebHostBuilder.BuildWebApplication(args).Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
