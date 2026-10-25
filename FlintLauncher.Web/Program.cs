// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// One-line wrapper around the WebHostBuilder factory: integration tests host the exact same
// app in-process through FlintLauncher.Web.WebHostBuilder.BuildWebApplication(args).

using WebHostBuilder = FlintLauncher.Web.WebHostBuilder;

WebHostBuilder.BuildWebApplication(args).Run();

// Exposed for WebApplicationFactory-based integration tests.
namespace FlintLauncher.Web
{
    public class Program;
}
