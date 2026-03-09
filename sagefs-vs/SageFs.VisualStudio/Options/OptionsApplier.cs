namespace SageFs.VisualStudio.Options;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;

/// <summary>
/// Reads the user-configured daemon URL from Tools → Options → SageFs and updates
/// <see cref="Core.SageFsClient"/> and daemon.json so the net472 MEF assembly
/// picks up the correct port on the next SSE reconnect.
///
/// On VS startup the URL is already loaded by <see cref="SageFsExtension"/> from
/// daemon.json. This part persists any option change so it survives the next restart.
/// </summary>
[VisualStudioContribution]
internal class OptionsApplier : ExtensionPart
{
    private readonly SageFsOptions _options;
    private readonly Core.SageFsClient _client;

    public OptionsApplier(SageFsOptions options, Core.SageFsClient client)
    {
        _options = options;
        _client = client;
    }

    protected override async Task InitializeAsync(CancellationToken ct)
    {
        await base.InitializeAsync(ct);

        try
        {
            // If DaemonUrl was updated since InitializeServices ran (e.g., user changed
            // Tools → Options → SageFs and reloaded without restarting VS), apply it now.
            var port = SageFsOptions.ParsePort(_options.DaemonUrl) ?? Core.Constants.DefaultMcpPort;
            if (port != _client.McpPort)
            {
                _client.McpPort = port;
                _client.DashboardPort = port + 1;
                SageFsExtension.WriteDaemonJson(_options.DaemonUrl);
                System.Diagnostics.Debug.WriteLine(
                    $"[SageFs] OptionsApplier: updated daemon port to {port} from {_options.DaemonUrl}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SageFs] OptionsApplier: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
