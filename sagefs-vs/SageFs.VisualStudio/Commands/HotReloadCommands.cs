namespace SageFs.VisualStudio.Commands;

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW

[VisualStudioContribution]
internal class HotReloadToggleFileCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public HotReloadToggleFileCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HotReloadToggle.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.FileSystemWatcher, IconSettings.IconAndText),
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var filePath = textView.Document.Uri.LocalPath;
    if (string.IsNullOrEmpty(filePath) || !filePath.EndsWith(".fs"))
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ Hot reload only works with .fs files");
      return;
    }

    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    if (sessions.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active session");
      return;
    }

    // Document-owning session selection: use the session whose working dir
    // contains the active file — not sessions[0] (the multi-session defect).
    var sessionId = Core.BufferChangeRequestInterop.ResolveSessionId(sessions, filePath);
    if (sessionId is null)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No session owns this file");
      return;
    }

    var ok = await client.ToggleHotReloadAsync(sessionId, filePath, ct);
    if (output is not null)
      await output.WriteLineAsync(
        ok
          ? $"↻ Toggled hot reload for {Path.GetFileName(filePath)}"
          : $"✗ Failed to toggle hot reload for {Path.GetFileName(filePath)} — is the daemon running?");
  }
}

[VisualStudioContribution]
internal class HotReloadWatchAllCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public HotReloadWatchAllCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HotReloadWatchAll.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Watch, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    if (sessions.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active session");
      return;
    }

    var ok = await client.WatchAllAsync(sessions[0].Id, ct);
    if (output is not null)
      await output.WriteLineAsync(
        ok
          ? "● Watch All — all F# files now watched for hot reload"
          : "✗ Failed to watch all files — is the daemon running?");
  }
}

[VisualStudioContribution]
internal class HotReloadUnwatchAllCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public HotReloadUnwatchAllCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HotReloadUnwatchAll.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.StopFilter, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    if (sessions.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active session");
      return;
    }

    var ok = await client.UnwatchAllAsync(sessions[0].Id, ct);
    if (output is not null)
      await output.WriteLineAsync(
        ok
          ? "○ Unwatch All — hot reload stopped for all files"
          : "✗ Failed to unwatch all files — is the daemon running?");
  }
}

[VisualStudioContribution]
internal class HotReloadRefreshCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public HotReloadRefreshCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HotReloadRefresh.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.Refresh, IconSettings.IconAndText),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    if (sessions.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active session");
      return;
    }

    var ok = await client.RefreshHotReloadAsync(sessions[0].Id, ct);
    if (output is not null)
      await output.WriteLineAsync(
        ok
          ? "↻ Hot reload refreshed — re-evaluating watched files"
          : "✗ Failed to refresh hot reload — is the daemon running?");
  }
}

[VisualStudioContribution]
internal class HotReloadToggleDirectoryCommand : Command
{
  private readonly Core.SageFsClient client;
  private OutputChannel? output;

  public HotReloadToggleDirectoryCommand(Core.SageFsClient client) => this.client = client;

  public override CommandConfiguration CommandConfiguration => new("%SageFs.HotReloadToggleDirectory.DisplayName%")
  {
    Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    Icon = new(ImageMoniker.KnownValues.FolderOpened, IconSettings.IconAndText),
    VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveEditorContentType, ".+"),
  };

  public override async Task InitializeAsync(CancellationToken ct)
  {
    output = await Extensibility.Views().Output.CreateOutputChannelAsync("SageFs", ct);
    await base.InitializeAsync(ct);
  }

  public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
  {
    using var textView = await context.GetActiveTextViewAsync(ct);
    if (textView is null) return;

    var filePath = textView.Document.Uri.LocalPath;
    var directory = Path.GetDirectoryName(filePath);
    if (string.IsNullOrEmpty(directory))
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ Could not determine directory");
      return;
    }

    var sessions = (await client.GetSessionsAsync(ct)).ToList();
    if (sessions.Count == 0)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No active session");
      return;
    }

    var sessionId = Core.BufferChangeRequestInterop.ResolveSessionId(sessions, filePath);
    if (sessionId is null)
    {
      if (output is not null)
        await output.WriteLineAsync("⚠ No session owns this directory");
      return;
    }

    // Real toggle: unwatch when already watched, watch otherwise.
    // bool? — true = watching, false = unwatched, null = failure.
    var result = await client.ToggleDirectoryWatchAsync(sessionId, directory, ct);
    if (output is not null)
      await output.WriteLineAsync(
        result is null
          ? $"✗ Failed to toggle hot reload for directory: {directory} — is the daemon running?"
          : result.Value
            ? $"📂 Watching directory: {directory}"
            : $"⏹ Stopped watching directory: {directory}");
  }
}

#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW
