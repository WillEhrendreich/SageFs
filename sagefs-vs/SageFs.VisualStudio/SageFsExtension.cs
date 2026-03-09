// Thin C# shim — source generators require C#.
// All real logic lives in SageFs.VisualStudio.Core (F#).
namespace SageFs.VisualStudio;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

[VisualStudioContribution]
internal class SageFsExtension : Extension
{
  public override ExtensionConfiguration ExtensionConfiguration => new()
  {
    Metadata = new(
      id: "SageFs.VisualStudio.a3f9c1e2-7b5d-4e8a-9c1f-2d3e4f5a6b7c",
      version: this.ExtensionAssemblyVersion,
      publisherName: "WillEhrendreich",
      displayName: "SageFs — F# Live Development",
      description: """
        Live F# development environment for Visual Studio. Features:
        • Real-time test gutter markers (pass/fail/skip indicators)
        • F# IntelliSense completions powered by the SageFs daemon
        • TypeExplorer tool window with auto-refresh
        • Live test status panel with run policy controls
        • Inline eval adornments and squiggles
        • Daemon health notifications on startup

        Requires: SageFs CLI (dotnet tool install --global SageFs), VS 2022 17.14+
        """)
    {
      MoreInfo = "https://github.com/WillEhrendreich/SageFs",
      Icon = @"Assets\icon.png",
      PreviewImage = @"Assets\preview.png",
      Tags = ["F#", "fsharp", "repl", "live-coding", "testing", "functional"],
    },
  };

  protected override void InitializeServices(IServiceCollection serviceCollection)
  {
    base.InitializeServices(serviceCollection);

    // Write the daemon URL so the in-process MEF assembly (SageFs.VisualStudio.Editor)
    // can discover it without a direct project reference across TFM boundaries.
    // Uses %LOCALAPPDATA%\SageFs\daemon.json — survives session across VS restarts.
    int daemonPort = Core.Constants.DefaultMcpPort;
    var daemonUrl = $"http://localhost:{daemonPort}";
    var sageFsDir = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SageFs");
    System.IO.Directory.CreateDirectory(sageFsDir);
    System.IO.File.WriteAllText(
        System.IO.Path.Combine(sageFsDir, "daemon.json"),
        $"{{\"Url\":\"{daemonUrl}\"}}");

    serviceCollection.AddSingleton<Core.SageFsClient>();
    serviceCollection.AddSingleton<Core.EvalCancellation>();
    serviceCollection.AddSingleton<Core.LiveTestingSubscriber>(sp =>
    {
      var sub = new Core.LiveTestingSubscriber(daemonPort);
      sub.Start();
      return sub;
    });
    serviceCollection.AddSingleton<Core.SessionSubscriber>(sp =>
    {
      var sub = new Core.SessionSubscriber(daemonPort);
      sub.Start();
      return sub;
    });

    // Daemon startup health check: StatusBarManager (ExtensionPart) handles this via
    // constructor-injected SageFsClient and fires a 2-second delayed ping in InitializeAsync,
    // writing the result ("✓ connected" / "⚠ not running") to the SageFs output channel.
  }
}
