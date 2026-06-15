using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Logging;
using XenoAtom.Terminal;

namespace CodeAlta.App;

internal static class UiTaskDiagnostics
{
    public static Task ObserveAsync(Task task, string operation, Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(setStatus);

        return StartObservedAsync(() => ObserveCoreAsync(task, operation, setStatus));
    }

    public static Task ObserveAsync(Func<Task> taskFactory, string operation, Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(setStatus);

        return StartObservedAsync(
            () =>
            {
                try
                {
                    return ObserveCoreAsync(taskFactory(), operation, setStatus);
                }
                catch (Exception ex)
                {
                    return ObserveCoreAsync(Task.FromException(ex), operation, setStatus);
                }
            });
    }

    private static Task StartObservedAsync(Func<Task> taskFactory)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);

        var previousContext = SynchronizationContext.Current;
        var observedContext = SafeObservedSynchronizationContext.Wrap(previousContext);
        try
        {
            SynchronizationContext.SetSynchronizationContext(observedContext);
            return taskFactory();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static async Task ObserveCoreAsync(Task task, string operation, Action<string, bool, StatusTone> setStatus)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var message = SR.T("Unexpected failure while trying to {0}.", operation);
            CodeAltaApp.UiLogger.Error(ex, message);

            try
            {
                Terminal.Error.WriteLine($"[CodeAlta.UI] {message} {ex}");
            }
            catch
            {
            }

            try
            {
                setStatus(SR.T("{0} {1}", message, ex.Message), false, StatusTone.Error);
            }
            catch
            {
            }
        }
    }

    private sealed class SafeObservedSynchronizationContext : SynchronizationContext
    {
        private const string DetachedDispatcherMessage = "Dispatcher is not attached to a running TerminalApp.";
        private readonly SynchronizationContext? _inner;

        private SafeObservedSynchronizationContext(SynchronizationContext? inner)
        {
            _inner = inner;
        }

        public static SynchronizationContext? Wrap(SynchronizationContext? inner)
            => inner is null ? null : new SafeObservedSynchronizationContext(inner);

        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            try
            {
                _inner!.Post(
                    static callbackState =>
                    {
                        var (context, callback, callbackArgument) = ((SafeObservedSynchronizationContext, SendOrPostCallback, object?))callbackState!;
                        var previousContext = SynchronizationContext.Current;
                        try
                        {
                            SynchronizationContext.SetSynchronizationContext(context);
                            callback(callbackArgument);
                        }
                        finally
                        {
                            SynchronizationContext.SetSynchronizationContext(previousContext);
                        }
                    },
                    (this, d, state));
            }
            catch (InvalidOperationException ex) when (IsDetachedDispatcherException(ex))
            {
                // The terminal is already shutting down. Dropping the continuation avoids surfacing a
                // dispatcher crash after the app has exited; any useful UI update can no longer render.
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            try
            {
                _inner!.Send(
                    static callbackState =>
                    {
                        var (context, callback, callbackArgument) = ((SafeObservedSynchronizationContext, SendOrPostCallback, object?))callbackState!;
                        var previousContext = SynchronizationContext.Current;
                        try
                        {
                            SynchronizationContext.SetSynchronizationContext(context);
                            callback(callbackArgument);
                        }
                        finally
                        {
                            SynchronizationContext.SetSynchronizationContext(previousContext);
                        }
                    },
                    (this, d, state));
            }
            catch (InvalidOperationException ex) when (IsDetachedDispatcherException(ex))
            {
            }
        }

        public override SynchronizationContext CreateCopy()
            => this;

        private static bool IsDetachedDispatcherException(InvalidOperationException exception)
            => string.Equals(exception.Message, DetachedDispatcherMessage, StringComparison.Ordinal);
    }
}
