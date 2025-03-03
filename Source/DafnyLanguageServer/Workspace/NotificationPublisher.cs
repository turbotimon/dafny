
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Dafny.LanguageServer.Workspace.Notifications;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Dafny.LanguageServer.IntegrationTest.Util;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Microsoft.Dafny.LanguageServer.Workspace {
  public class NotificationPublisher : INotificationPublisher {
    private readonly ILogger<NotificationPublisher> logger;
    private readonly LanguageServerFilesystem filesystem;
    private readonly ILanguageServerFacade languageServer;
    private readonly DafnyOptions options;

    public NotificationPublisher(
      ILogger<NotificationPublisher> logger,
      ILanguageServerFacade languageServer,
      DafnyOptions options, LanguageServerFilesystem filesystem) {
      this.logger = logger;
      this.languageServer = languageServer;
      this.options = options;
      this.filesystem = filesystem;
    }

    public void PublishNotifications(IdeState previousState, IdeState state) {
      if (state.Version < previousState.Version) {
        return;
      }

      PublishDiagnostics(state);
      PublishProgress(previousState, state);
      PublishGhostness(previousState, state);
      foreach (var uri in state.OwnedUris) {
        PublishGutterIcons(uri, state);
      }
    }

    private void PublishProgress(IdeState previousState, IdeState state) {
      // Some global progress values, such as ResolutionSucceeded, will trigger the symbol progress to be displayed.
      // To ensure that the displayed progress is always up-to-date,
      // we must publish symbol progress before publishing the global one.

      // Better would be to have a single notification API with the schema { globalProgress, symbolProgress }
      // so this problem can not occur, although that would require the "symbolProgress" part to be able to contain a
      // "no-update" value to prevent having to send many duplicate symbolProgress updates.

      PublishSymbolProgress(previousState, state);
      PublishGlobalProgress(previousState, state);
    }

    private void PublishSymbolProgress(IdeState previousState, IdeState state) {
      foreach (var uri in state.OwnedUris.Concat(previousState.OwnedUris).Distinct()) {
        var previous = GetFileVerificationStatus(previousState, uri);
        var current = GetFileVerificationStatus(state, uri);

        if (Equals(current, previous)) {
          continue;
        }

        languageServer.TextDocument.SendNotification(DafnyRequestNames.VerificationSymbolStatus, current);
      }
    }

    private void PublishGlobalProgress(IdeState previousState, IdeState state) {
      foreach (var uri in state.OwnedUris) {

        var current = state.Status;
        var previous = previousState.Status;

        if (Equals(current, previous)) {
          continue;
        }

        languageServer.SendNotification(new CompilationStatusParams {
          Uri = uri,
          Version = filesystem.GetVersion(uri),
          Status = current,
          Message = null
        });
      }
    }

    private FileVerificationStatus GetFileVerificationStatus(IdeState state, Uri uri) {
      var verificationResults = state.GetVerificationResults(uri);
      return new FileVerificationStatus(uri, filesystem.GetVersion(uri),
        verificationResults.Select(kv => GetNamedVerifiableStatuses(kv.Key, kv.Value)).
            OrderBy(s => s.NameRange.Start).ToList());
    }

    private static NamedVerifiableStatus GetNamedVerifiableStatuses(Range canVerify, IdeVerificationResult result) {
      const PublishedVerificationStatus nothingToVerifyStatus = PublishedVerificationStatus.Correct;
      var status = result.PreparationProgress switch {
        VerificationPreparationState.NotStarted => PublishedVerificationStatus.Stale,
        VerificationPreparationState.InProgress => PublishedVerificationStatus.Queued,
        VerificationPreparationState.Done =>
          result.Implementations.Values.Select(v => v.Status).Aggregate(nothingToVerifyStatus, Combine),
        _ => throw new ArgumentOutOfRangeException()
      };

      return new(canVerify, status);
    }

    public static PublishedVerificationStatus Combine(PublishedVerificationStatus first, PublishedVerificationStatus second) {
      var max = new[] { first, second }.Max();
      var min = new[] { first, second }.Min();
      return max >= PublishedVerificationStatus.Error ? min : max;
    }

    private readonly ConcurrentDictionary<Uri, IList<Diagnostic>> publishedDiagnostics = new();

    private void PublishDiagnostics(IdeState state) {
      // All root uris are added because we may have to publish empty diagnostics for owned uris.
      var sources = state.GetDiagnosticUris().Concat(state.OwnedUris).Distinct();

      var projectDiagnostics = new List<Diagnostic>();
      foreach (var uri in sources) {
        var current = state.GetDiagnosticsForUri(uri);
        var ownedUri = state.OwnedUris.Contains(uri);
        if (ownedUri) {
          if (uri == state.Input.Project.Uri) {
            // Delay publication of project diagnostics,
            // since it also serves as a bucket for diagnostics from unowned files
            projectDiagnostics.AddRange(current);
          } else {
            PublishForUri(uri, current.ToArray());
          }
        } else {
          var errors = current.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
          if (!errors.Any()) {
            continue;
          }

          projectDiagnostics.Add(new Diagnostic {
            Range = new Range(0, 0, 0, 1),
            Message = $"the referenced file {uri.LocalPath} contains error(s) but is not owned by this project. The first error is:\n{errors.First().Message}",
            Severity = DiagnosticSeverity.Error,
            Source = MessageSource.Parser.ToString()
          });
        }
      }

      PublishForUri(state.Input.Project.Uri, projectDiagnostics.ToArray());

      void PublishForUri(Uri publishUri, Diagnostic[] diagnostics) {
        var previous = publishedDiagnostics.GetOrDefault(publishUri, Enumerable.Empty<Diagnostic>);
        if (!previous.SequenceEqual(diagnostics, new DiagnosticComparer())) {
          if (diagnostics.Any()) {
            publishedDiagnostics[publishUri] = diagnostics;
          } else {
            // Prevent memory leaks by cleaning up previous state when it's the IDE's initial state.
            publishedDiagnostics.TryRemove(publishUri, out _);
          }

          languageServer.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams {
            Uri = publishUri,
            Version = filesystem.GetVersion(publishUri),
            Diagnostics = diagnostics,
          });
        }
      }
    }


    private readonly Dictionary<Uri, VerificationStatusGutter> previouslyPublishedIcons = new();

    private void PublishGutterIcons(Uri uri, IdeState state) {
      if (!options.Get(GutterIconAndHoverVerificationDetailsManager.LineVerificationStatus)) {
        return;
      }

      if (state.Status == CompilationStatus.Parsing) {
        return;
      }

      bool verificationStarted = state.Status == CompilationStatus.ResolutionSucceeded;

      var errors = state.StaticDiagnostics.GetOrDefault(uri, Enumerable.Empty<Diagnostic>).
        Where(x => x.Severity == DiagnosticSeverity.Error).ToList();
      var tree = state.VerificationTrees.GetValueOrDefault(uri);
      if (tree == null) {
        return;
      }

      var linesCount = tree.Range.End.Line + 1;
      var fileVersion = filesystem.GetVersion(uri);
      if (linesCount == 0) {
        return;
      }

      var verificationStatusGutter = VerificationStatusGutter.ComputeFrom(
        DocumentUri.From(uri),
        fileVersion,
        tree.Children,
        errors,
        linesCount,
        verificationStarted
      );
      if (logger.IsEnabled(LogLevel.Trace)) {
        var icons = string.Join(' ', verificationStatusGutter.PerLineStatus.Select(s => LineVerificationStatusToString[s]));
        logger.LogDebug($"Sending gutter icons for compilation {state.Input.Project.Uri}, comp version {state.Version}, file version {fileVersion}" +
                        $"icons: {icons}\n" +
                        $"stacktrace:\n{Environment.StackTrace}");
      }

      lock (previouslyPublishedIcons) {
        var previous = previouslyPublishedIcons.GetValueOrDefault(uri);
        if (previous == null || !previous.PerLineStatus.SequenceEqual(verificationStatusGutter.PerLineStatus)) {
          previouslyPublishedIcons[uri] = verificationStatusGutter;
          languageServer.TextDocument.SendNotification(verificationStatusGutter);
        }
      }
    }

    public static Dictionary<LineVerificationStatus, string> LineVerificationStatusToString = new() {
      { LineVerificationStatus.Nothing, "   " },
      { LineVerificationStatus.Scheduled, " . " },
      { LineVerificationStatus.Verifying, " S " },
      { LineVerificationStatus.VerifiedObsolete, " I " },
      { LineVerificationStatus.VerifiedVerifying, " $ " },
      { LineVerificationStatus.Verified, " | " },
      { LineVerificationStatus.ErrorContextObsolete, "[I]" },
      { LineVerificationStatus.ErrorContextVerifying, "[S]" },
      { LineVerificationStatus.ErrorContext, "[ ]" },
      { LineVerificationStatus.AssertionFailedObsolete, "[-]" },
      { LineVerificationStatus.AssertionFailedVerifying, "[~]" },
      { LineVerificationStatus.AssertionFailed, "[=]" },
      { LineVerificationStatus.AssertionVerifiedInErrorContextObsolete, "[o]" },
      { LineVerificationStatus.AssertionVerifiedInErrorContextVerifying, "[Q]" },
      { LineVerificationStatus.AssertionVerifiedInErrorContext, "[O]" },
      { LineVerificationStatus.ResolutionError, @"/!\" },
      { LineVerificationStatus.Skipped, @" ? " }
    };

    private void PublishGhostness(IdeState previousState, IdeState state) {

      var newParams = state.GhostRanges;
      var previousParams = previousState.GhostRanges;
      var uris = previousParams.Keys.Concat(newParams.Keys);
      foreach (var uri in uris) {
        var previous = previousParams.GetValueOrDefault(uri) ?? Enumerable.Empty<Range>().ToList();
        var current = newParams.GetValueOrDefault(uri) ?? Enumerable.Empty<Range>().ToList();
        if (previous.SequenceEqual(current)) {
          continue;
        }
        languageServer.TextDocument.SendNotification(new GhostDiagnosticsParams {
          Uri = uri,
          Version = state.Version,
          Diagnostics = current.Select(r => new Diagnostic {
            Range = r
          }).ToArray(),
        });
      }
    }
  }
}
