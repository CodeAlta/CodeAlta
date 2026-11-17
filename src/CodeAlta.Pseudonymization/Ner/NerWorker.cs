using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.NativeEngines;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CodeAlta.Pseudonymization.Ner;

/// <summary>What the worker is asked to load.</summary>
/// <param name="ModelDirectory">The folder of the model.</param>
/// <param name="OnnxRuntime">The full path of the ONNX Runtime library: it is never loaded by its bare name.</param>
/// <param name="Threads">The threads of a run.</param>
public sealed record NerWorkerLoad(string ModelDirectory, string OnnxRuntime, int Threads);

/// <summary>Sequences of ids to classify.</summary>
/// <param name="Sequences">The sequences.</param>
internal sealed record NerWorkerRequest(IReadOnlyList<int[]> Sequences);

/// <summary>The labels of classified sequences: for each, its labels and their probabilities.</summary>
/// <param name="Labels">The label of each token.</param>
/// <param name="Probabilities">The probability of each label.</param>
internal sealed record NerWorkerAnswer(IReadOnlyList<int[]> Labels, IReadOnlyList<float[]> Probabilities);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NerWorkerLoad))]
[JsonSerializable(typeof(NerWorkerRequest))]
[JsonSerializable(typeof(NerWorkerAnswer))]
internal sealed partial class NerWorkerJsonContext : JsonSerializerContext;

/// <summary>The kinds of the frames between the host and the worker of the models.</summary>
internal static class NerFrameKind
{
    public const byte Load = 1;
    public const byte Loaded = 2;
    public const byte Classify = 3;
    public const byte Labels = 4;
    public const byte Error = 5;
    public const byte Exit = 6;
}

/// <summary>
/// The worker the models that find names run in: a process of their own, so that a failure of the native runtime fails a
/// request and never the application, and so that the memory of a model is given back when it ends. It answers frames on
/// its standard streams, one at a time, until they close or it is asked to exit.
/// </summary>
public static class NerWorker
{
    /// <summary>The argument that starts the desktop application as the worker.</summary>
    public const string Option = "--pseudonymizer-worker";

    /// <summary>Runs the worker on the standard streams of the process.</summary>
    /// <returns>The exit code of the process.</returns>
    public static int RunProcess()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Run(input, output, static load => new OnnxTokenClassifier(load));
        return 0;
    }

    /// <summary>Answers the frames of a stream until it ends or asks to exit.</summary>
    /// <param name="input">The frames of the host.</param>
    /// <param name="output">Where the answers go.</param>
    /// <param name="load">Loads a model.</param>
    internal static void Run(Stream input, Stream output, Func<NerWorkerLoad, IOnnxTokenClassifier> load)
    {
        IOnnxTokenClassifier? model = null;
        try
        {
            while (true)
            {
                WorkerFrame? next;
                try { next = WorkerFrames.Read(input); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException) { return; }
                if (next is not { } frame || frame.Kind == NerFrameKind.Exit) return;
                try
                {
                    switch (frame.Kind)
                    {
                        case NerFrameKind.Load:
                            model?.Dispose();
                            model = null;
                            var request = JsonSerializer.Deserialize(frame.Content, NerWorkerJsonContext.Default.NerWorkerLoad) ?? throw new InvalidOperationException("No model was given.");
                            model = load(request);
                            WorkerFrames.Write(output, NerFrameKind.Loaded, frame.Correlation, []);
                            break;
                        case NerFrameKind.Classify when model is not null:
                            var sequences = JsonSerializer.Deserialize(frame.Content, NerWorkerJsonContext.Default.NerWorkerRequest) ?? throw new InvalidOperationException("No sequences were given.");
                            var answer = model.Classify(sequences.Sequences);
                            WorkerFrames.Write(output, NerFrameKind.Labels, frame.Correlation, JsonSerializer.SerializeToUtf8Bytes(answer, NerWorkerJsonContext.Default.NerWorkerAnswer));
                            break;
                        case NerFrameKind.Classify:
                            WorkerFrames.Write(output, NerFrameKind.Error, frame.Correlation, "No model is loaded."u8);
                            break;
                    }
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    // A model that cannot be loaded or a request it refuses fails that request; the worker goes on.
                    WorkerFrames.Write(output, NerFrameKind.Error, frame.Correlation, Encoding.UTF8.GetBytes(exception.Message));
                }

                output.Flush();
            }
        }
        finally
        {
            model?.Dispose();
        }
    }
}

/// <summary>A model loaded in the worker.</summary>
internal interface IOnnxTokenClassifier : IDisposable
{
    /// <summary>Classifies sequences of ids.</summary>
    NerWorkerAnswer Classify(IReadOnlyList<int[]> sequences);
}

/// <summary>
/// A token classification model on the ONNX Runtime that speech and search share, through
/// <c>Microsoft.ML.OnnxRuntime.Managed</c>: its <c>input_ids</c>, <c>attention_mask</c> and, when it has one,
/// <c>token_type_ids</c>; its first output, the logits of each token.
/// </summary>
internal sealed class OnnxTokenClassifier : IOnnxTokenClassifier
{
    private readonly InferenceSession _session;
    private readonly bool _tokenTypes;
    private readonly int _pad;
    private readonly string _output;

    public OnnxTokenClassifier(NerWorkerLoad load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (!File.Exists(load.OnnxRuntime)) throw new InvalidOperationException("The ONNX Runtime library is missing.");
        // The library is loaded by its full path, never by its bare name: Windows can have another onnxruntime.dll.
        OrtEnv.DisableDllImportResolver = true;
        var path = load.OnnxRuntime;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(OrtEnv).Assembly, (name, _, _) =>
                name.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(path) : IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            // A process sets the resolver once: a worker asked to load another model runs it on the runtime it loaded first,
            // which is the one runtime of the application.
        }
        var description = NerModelDescription.Load(load.ModelDirectory);
        var tokenizer = NerTokenizer.Load(load.ModelDirectory, description.Tokenizer);
        _pad = tokenizer.PadId;
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(load.Threads, 1, 8), InterOpNumThreads = 1 };
        _session = new InferenceSession(Path.Combine(load.ModelDirectory, description.Model), options);
        _tokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _output = _session.OutputMetadata.Keys.First();
    }

    public NerWorkerAnswer Classify(IReadOnlyList<int[]> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count == 0) return new NerWorkerAnswer([], []);
        var length = sequences.Max(static sequence => sequence.Length);
        var ids = new DenseTensor<long>([sequences.Count, length]);
        var mask = new DenseTensor<long>([sequences.Count, length]);
        for (var row = 0; row < sequences.Count; row++)
        {
            for (var column = 0; column < length; column++)
            {
                var inside = column < sequences[row].Length;
                ids[row, column] = inside ? sequences[row][column] : _pad;
                mask[row, column] = inside ? 1 : 0;
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask) };
        if (_tokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([sequences.Count, length])));
        using var results = _session.Run(inputs);
        var logits = results.First(result => result.Name == _output).AsTensor<float>();
        var classes = logits.Dimensions[2];
        var labels = new List<int[]>(sequences.Count);
        var probabilities = new List<float[]>(sequences.Count);
        for (var row = 0; row < sequences.Count; row++)
        {
            var count = sequences[row].Length;
            var best = new int[count];
            var probability = new float[count];
            for (var column = 0; column < count; column++)
            {
                // The softmax of the logits of the token: the probability of the label it gives.
                var max = float.NegativeInfinity;
                var argmax = 0;
                for (var label = 0; label < classes; label++)
                {
                    var value = logits[row, column, label];
                    if (value > max) (max, argmax) = (value, label);
                }

                double sum = 0;
                for (var label = 0; label < classes; label++) sum += Math.Exp(logits[row, column, label] - max);
                best[column] = argmax;
                probability[column] = (float)(1 / sum);
            }

            labels.Add(best);
            probabilities.Add(probability);
        }

        return new NerWorkerAnswer(labels, probabilities);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Runs a model in the worker process: the requests of the sessions go to it one at a time. A worker that ends or fails
/// is started again at the next request; a request it cannot answer fails, and the session sends nothing.
/// </summary>
public sealed class NerWorkerClient : ITokenClassifier, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<IWorkerProcess> _start;
    private readonly NerWorkerLoad _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IWorkerProcess? _worker;
    private int _correlation;
    private bool _disposed;

    /// <summary>Creates the client.</summary>
    /// <param name="start">Starts a worker.</param>
    /// <param name="load">The model it loads.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public NerWorkerClient(Func<IWorkerProcess> start, NerWorkerLoad load)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(load);
        _start = start;
        _load = load;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TokenLabel[]>> ClassifyAsync(IReadOnlyList<int[]> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var worker = await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);
            var frame = await ExchangeAsync(worker, NerFrameKind.Classify,
                JsonSerializer.SerializeToUtf8Bytes(new NerWorkerRequest(sequences), NerWorkerJsonContext.Default.NerWorkerRequest), RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (frame.Kind != NerFrameKind.Labels) throw new InvalidOperationException("The model failed: " + frame.Text);
            var answer = JsonSerializer.Deserialize(frame.Content, NerWorkerJsonContext.Default.NerWorkerAnswer) ?? throw new InvalidOperationException("The model gave no labels.");
            if (answer.Labels.Count != sequences.Count || answer.Probabilities.Count != sequences.Count) throw new InvalidOperationException("The model answered for another number of sequences.");
            return [.. answer.Labels.Select((labels, index) => labels.Select((label, token) => new TokenLabel(label, answer.Probabilities[index][token])).ToArray())];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<IWorkerProcess> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_worker is { } running) return running;
        var worker = _start();
        try
        {
            var frame = await ExchangeAsync(worker, NerFrameKind.Load,
                JsonSerializer.SerializeToUtf8Bytes(_load, NerWorkerJsonContext.Default.NerWorkerLoad), LoadTimeout, cancellationToken, owned: false).ConfigureAwait(false);
            if (frame.Kind != NerFrameKind.Loaded) throw new InvalidOperationException("The model did not load: " + frame.Text);
        }
        catch
        {
            Stop(worker);
            throw;
        }

        _worker = worker;
        return worker;
    }

    // One frame out, one frame back. A worker that does not answer in time, or whose streams fail, is stopped: the next
    // request starts another.
    private async ValueTask<WorkerFrame> ExchangeAsync(IWorkerProcess worker, byte kind, byte[] content, TimeSpan timeout, CancellationToken cancellationToken, bool owned = true)
    {
        var correlation = ++_correlation;
        try
        {
            var exchange = Task.Run(() =>
            {
                WorkerFrames.Write(worker.Input, kind, correlation, content);
                worker.Input.Flush();
                var answer = WorkerFrames.Read(worker.Output) ?? throw new IOException("The worker of the model ended.");
                return answer.Correlation == correlation ? answer : throw new InvalidDataException("The worker of the model answered another request.");
            }, CancellationToken.None);
            return await exchange.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            if (owned) Forget();
            else Stop(worker);
            if (exception is OperationCanceledException) throw;
            throw new InvalidOperationException("The worker of the model failed: " + exception.Message, exception);
        }
    }

    private void Forget()
    {
        if (_worker is { } worker) Stop(worker);
        _worker = null;
    }

    private static void Stop(IWorkerProcess worker)
    {
        try
        {
            WorkerFrames.Write(worker.Input, NerFrameKind.Exit, 0, []);
            worker.Input.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        try { worker.Kill(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        worker.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            Forget();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }
}
