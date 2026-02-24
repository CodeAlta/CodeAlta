using CodeNoesis.CodexSdk;
using CodeNoesis.CodexSdk.V2;
using LLama;
using LLama.Common;
using LLama.Native;
using System.Diagnostics;
using System.Numerics.Tensors;
using System.Text.Json;

/*
NativeLibraryConfig.All.WithLogCallback((level, message) =>
{
});

//NativeLibraryConfig.All.WithAvx(AvxLevel.Avx2);

// var modelPath = @"C:\code\huggingface\jina-embeddings-v5-text-nano-text-matching-GGUF\v5-nano-text-matching-Q8_0.gguf";


//var modelPath = @"C:\code\huggingface\nomic-embed-code-GGUF\nomic-embed-code.Q6_K.gguf";
//var modelPath = @"C:\code\huggingface\nomic-embed-text-v1.5-GGUF\nomic-embed-text-v1.5.Q6_K.gguf";
var modelPath = @"C:\code\huggingface\embeddinggemma-300M-GGUF\embeddinggemma-300M-Q8_0.gguf";

var @params = new ModelParams(modelPath)
{
    // Embedding models can return one embedding per token, or all of them can be combined ("pooled") into
    // one single embedding. Setting PoolingType to "Mean" will combine all of the embeddings using mean average.
    PoolingType = LLamaPoolingType.Last,
};
using var weights = LLamaWeights.LoadFromFile(@params);
var embedder = new LLamaEmbedder(weights, @params);

var clock = Stopwatch.StartNew();
for (int i = 0; i < 100; i++)
{
    clock.Restart();
    var results = await embedder.GetEmbeddings(
        """
        title: Reducing allocations | text: path=docs/performance.md
        type=markdown

        Use Span<T> and ArrayPool<T> for high throughput parsing. Avoid LINQ in tight loops.
        """);
    clock.Stop();
    var elapsed = clock.Elapsed;


    Console.WriteLine($"Got {results.Count} embeddings, each of dimension {results[0].Length} in {elapsed.TotalMilliseconds}ms");

    var anotherResults = await embedder.GetEmbeddings("task: search result | query: How can I reduce allocations in our C# parser?");
    Console.WriteLine($"Got {anotherResults.Count} embeddings, each of dimension {anotherResults[0].Length}.");

    var a = results.Single().ToArray();
    var b = anotherResults.Single().ToArray();

    EmbeddingSimilarity.NormalizeL2(a);
    EmbeddingSimilarity.NormalizeL2(b);

    Console.WriteLine($"Similarities: {EmbeddingSimilarity.DotSimilarityNormalized(a, b)}");
}

return;
*/

var options = CodexClient.CreateJsonSerializerOptions();

var test = new ThreadStartParams()
{
    Config = JsonSerializer.SerializeToElement("test", options)
};

var result = JsonSerializer.Serialize(test, options);
Console.WriteLine(result);

var codexClient = await CodexClient.StartAsync(new ClientInfo
{
    Name = "CodeNoesis",
    Version = "1.0.0",
    Title = "CodeNoesis App"
});

var config = await codexClient.ConfigReadAsync(new ConfigReadParams()
{
    Cwd = AppContext.BaseDirectory
});
Console.WriteLine(config);

var models = await codexClient.ModelListAsync(new ModelListParams());
foreach (var model in models.Data)
{
    Console.WriteLine(model);
}

return;
var accountRead = await codexClient.AccountReadAsync(new GetAccountParams());
Console.WriteLine(accountRead.Account);

var rateLimit = await codexClient.AccountRateLimitsReadAsync();
Console.WriteLine($"Rate limit: {rateLimit}");


var threadList = await codexClient.ThreadListAsync(new ThreadListParams()
{
    //Cwd = @"C:\code\XenoAtom\XenoAtom.CommandLine"
});


foreach(var thread in threadList.Data)
{
    Console.WriteLine($"Thread: {thread.Id} - ModelProvider: {thread.ModelProvider}, Cwd: {thread.Cwd}, CliVersion: {thread.CliVersion}, CreatedAt: {thread.CreatedAt} TurnsCount: {thread.Turns.Count}, Preview: {thread.Preview}, Source: {thread.Source}, GitInfo: {thread.GitInfo}, Path: {thread.Path}");
}

public static class EmbeddingSimilarity
{
    // Cosine similarity = dot(a,b) / (||a|| * ||b||)
    // If you pre-normalize embeddings to unit length, cosine similarity = dot(a,b).
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vectors must have same length.");

        float dot = TensorPrimitives.Dot(a, b);
        float normA = MathF.Sqrt(TensorPrimitives.Dot(a, a));
        float normB = MathF.Sqrt(TensorPrimitives.Dot(b, b));

        // Avoid division by zero
        if (normA == 0 || normB == 0) return 0;

        return dot / (normA * normB);
    }

    // Normalize in-place: v = v / ||v||
    public static void NormalizeL2(Span<float> v)
    {
        float norm = MathF.Sqrt(TensorPrimitives.Dot(v, v));
        if (norm == 0) return;
        TensorPrimitives.Divide(v, norm, v);
    }

    // If both vectors are already normalized, this is the fastest similarity:
    public static float DotSimilarityNormalized(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TensorPrimitives.Dot(a, b);
}