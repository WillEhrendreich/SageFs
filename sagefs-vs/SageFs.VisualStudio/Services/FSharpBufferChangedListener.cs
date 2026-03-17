namespace SageFs.VisualStudio.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

[VisualStudioContribution]
internal sealed class FSharpBufferChangedListener : ExtensionPart, ITextViewChangedListener
{
  private readonly Core.SageFsClient client;
  private readonly ConcurrentDictionary<string, CancellationTokenSource> pendingByFile = new(StringComparer.OrdinalIgnoreCase);

  public FSharpBufferChangedListener(Core.SageFsClient client)
  {
    this.client = client;
  }

  public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
  {
    AppliesTo =
    [
      DocumentFilter.FromGlobPattern("**/*.fs", true),
      DocumentFilter.FromGlobPattern("**/*.fsi", true),
    ],
  };

  public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
  {
    var document = args.AfterTextView.Document;
    var uri = document.Uri;
    if (uri is null || !uri.IsFile)
      return;

    var filePath = uri.LocalPath;
    if (!IsCompiledSourceFile(filePath))
      return;

    var content = document.Text.CopyToString();
    using var debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    pendingByFile.AddOrUpdate(filePath, debounceCts, (_, existing) =>
    {
      existing.Cancel();
      existing.Dispose();
      return debounceCts;
    });

    try
    {
      await Task.Delay(300, debounceCts.Token);

      var sessions = await client.GetSessionsAsync(debounceCts.Token);
      var request = Core.BufferChangeRequestInterop.TryCreate(sessions, filePath, content);

      if (request is null)
        return;

      await client.PostBufferChangedAsync(request.Value, debounceCts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
      if (pendingByFile.TryGetValue(filePath, out var current) && ReferenceEquals(current, debounceCts))
      {
        pendingByFile.TryRemove(filePath, out _);
      }
    }
  }

  private static bool IsCompiledSourceFile(string filePath)
  {
    return filePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
      || filePath.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase);
  }
}
