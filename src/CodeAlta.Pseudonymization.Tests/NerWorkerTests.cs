using System.IO.Pipes;
using CodeAlta.NativeEngines;
using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Pseudonymization.Tests;

[TestClass]
public sealed class NerWorkerTests
{
    // The folder of a token classification model (bert-base-NER, with its ner-model.json) and the ONNX Runtime library:
    // the test runs a real model when both are given, and is skipped otherwise.
    private const string ModelVariable = "CODEALTA_TEST_NER_MODEL";
    private const string RuntimeVariable = "CODEALTA_TEST_ONNXRUNTIME";
    private const string FrenchModelVariable = "CODEALTA_TEST_NER_MODEL_FR";

    [TestMethod]
    public async Task ARealModel_InTheWorker_FindsPeoplePlacesAndOrganizations()
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var runtime = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(model) || !File.Exists(runtime))
            Assert.Inconclusive($"Set {ModelVariable} and {RuntimeVariable} to run a real model.");

        var description = NerModelDescription.Load(model!);
        var tokenizer = NerTokenizer.Load(model!, description.Tokenizer);
        using var client = new NerWorkerClient(static () => new InProcessWorker(), new NerWorkerLoad(model!, runtime!, 2));
        var recognizer = new TokenClassificationRecognizer(description, tokenizer, client);
        const string text = "John Smith works for Microsoft in London with Mary O'Neil.";

        var spans = await recognizer.AnalyzeAsync(text, CancellationToken.None);

        var found = spans.Select(span => $"{span.Entity}:{text.Substring(span.Start, span.Length)}").ToArray();
        CollectionAssert.IsSubsetOf(new[] { "PERSONNE:John Smith", "ORGANISATION:Microsoft", "LIEU:London" }, found, string.Join(" | ", found));
        Assert.IsTrue(spans.All(static span => span.Score is > 0.5 and <= 1), "Each result has the mean probability of its tokens.");
    }

    [TestMethod]
    public async Task CamembertInTheWorker_FindsTheNamesOfAFrenchAct()
    {
        var model = Environment.GetEnvironmentVariable(FrenchModelVariable);
        var runtime = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(runtime) || !Directory.Exists(model) || !File.Exists(runtime))
            Assert.Inconclusive($"Set {FrenchModelVariable} and {RuntimeVariable} to run CamemBERT-NER.");

        var description = NerModelDescription.Load(model!);
        var tokenizer = NerTokenizer.Load(model!, description.Tokenizer);
        using var client = new NerWorkerClient(static () => new InProcessWorker(), new NerWorkerLoad(model!, runtime!, 2));
        var recognizer = new TokenClassificationRecognizer(description, tokenizer, client);
        const string text = "Maître Durand reçoit Madame Sophie Martin, demeurant à Montauban, et la société Crédit Agricole.";

        var spans = await recognizer.AnalyzeAsync(text, CancellationToken.None);

        var found = spans.Select(span => $"{span.Entity}:{text.Substring(span.Start, span.Length)}").ToArray();
        CollectionAssert.IsSubsetOf(new[] { "PERSONNE:Durand", "PERSONNE:Sophie Martin", "LIEU:Montauban" }, found, string.Join(" | ", found));
    }

    [TestMethod]
    public async Task AWorkerThatNoLongerReads_IsKilled_AndNothingIsWrittenToItFirst()
    {
        var worker = new StuckWorker();
        var client = new NerWorkerClient(() => worker, new NerWorkerLoad("model", "runtime", 1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)); // not a limit

        var classify = client.ClassifyAsync([[1, 2]], cancellation.Token).AsTask();

        // A frame written to a worker that reads nothing never ends: the request would never give its turn back.
        Assert.AreSame(classify, await Task.WhenAny(classify, Task.Delay(TestTime.Seconds(30))), "The request ends when it is cancelled.");
        await Assert.ThrowsAsync<OperationCanceledException>(() => classify);
        Assert.IsTrue(worker.Killed);
        Assert.AreEqual(1, worker.Writes, "Only the request was written: nothing after the worker stopped reading.");
        client.Dispose();
    }

    // A worker that reads nothing and answers nothing: what is written to it waits until it is killed.
    private sealed class StuckWorker : IWorkerProcess
    {
        private readonly ManualResetEventSlim _killed = new();
        private int _writes;

        public StuckWorker()
        {
            Input = new StuckStream(this);
            Output = new StuckStream(this);
        }

        public Stream Input { get; }

        public Stream Output { get; }

        public bool Killed => _killed.IsSet;

        public int Writes => Volatile.Read(ref _writes);

        public void Kill() => _killed.Set();

        public void Dispose()
        {
        }

        private sealed class StuckStream(StuckWorker owner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                owner._killed.Wait();
                throw new IOException("The worker was killed.");
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Interlocked.Increment(ref owner._writes);
                owner._killed.Wait();
                throw new IOException("The worker was killed.");
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    // The worker on pipes of this process, as the application runs it in a process of its own.
    private sealed class InProcessWorker : IWorkerProcess
    {
        private readonly AnonymousPipeServerStream _toWorker = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromWorker = new(PipeDirection.In);
        private readonly Thread _thread;

        public InProcessWorker()
        {
            var input = new AnonymousPipeClientStream(PipeDirection.In, _toWorker.ClientSafePipeHandle);
            var output = new AnonymousPipeClientStream(PipeDirection.Out, _fromWorker.ClientSafePipeHandle);
            _thread = new Thread(() =>
            {
                using (input)
                using (output)
                {
                    NerWorker.Run(input, output, static load => new OnnxTokenClassifier(load));
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Stream Input => _toWorker;

        public Stream Output => _fromWorker;

        public void Kill()
        {
        }

        public void Dispose()
        {
            _toWorker.Dispose();
            _thread.Join(TimeSpan.FromSeconds(10));
            _fromWorker.Dispose();
        }
    }
}
