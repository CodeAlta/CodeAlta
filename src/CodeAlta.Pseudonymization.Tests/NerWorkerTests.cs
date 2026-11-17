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
