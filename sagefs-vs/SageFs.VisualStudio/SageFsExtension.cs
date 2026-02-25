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
    serviceCollection.AddSingleton<Core.SageFsClient>();
    serviceCollection.AddSingleton<Core.LiveTestingSubscriber>(sp =>
    {
      var sub = new Core.LiveTestingSubscriber(37749);
      sub.Start();
      return sub;
    });
  }
}
