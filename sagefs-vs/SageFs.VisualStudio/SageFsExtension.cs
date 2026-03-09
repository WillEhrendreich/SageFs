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
      description: "Inline eval, session management, and hot-reload for F# via SageFs daemon"),
  };

  protected override void InitializeServices(IServiceCollection serviceCollection)
  {
    base.InitializeServices(serviceCollection);

    // Write the daemon URL so the in-process MEF assembly (SageFs.VisualStudio.Editor)
    // can discover it without a direct project reference across TFM boundaries.
    const int daemonPort = 37749;
    var daemonUrl = $"http://localhost:{daemonPort}";
    var portFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sagefs-port.txt");
    System.IO.File.WriteAllText(portFile, daemonUrl);

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
  }
}
