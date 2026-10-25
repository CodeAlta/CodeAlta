using System.Collections;
using System.Reflection;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// The store hands out copies of the documents it keeps: a copy must carry every property of every type the document
/// reaches, and share nothing that can be changed. A property added to a document type fails here until it is copied.
/// </summary>
[TestClass]
public sealed class CodeAltaConfigDocumentCopyTests
{
    [TestMethod]
    public void Copy_CarriesEveryPropertyOfEveryType_AndSharesNothingThatCanBeChanged()
    {
        var filler = new Filler();
        var original = (CodeAltaConfigDocument)filler.Fill(typeof(CodeAltaConfigDocument), "document");

        var copy = CodeAltaConfigDocumentCopy.Copy(original);

        AssertEquivalent(original, copy, "document");
        CollectionAssert.IsSubsetOf(DocumentTypes().ToArray(), filler.Filled.ToArray(), "The test fills every type the document reaches.");
    }

    [TestMethod]
    public void Copy_OfAParsedConfiguration_WritesTheSameToml()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-config-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            File.WriteAllText(store.ConfigPath, CodeAltaConfigStoreCacheTests.RichConfig);
            var loaded = store.LoadGlobal();
            var copy = CodeAltaConfigDocumentCopy.Copy(loaded);
            Assert.AreEqual(TomlSerializer.Serialize(loaded), TomlSerializer.Serialize(copy));
            AssertEquivalent(loaded, copy, "document");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The classes of the configuration model, as the document reaches them through its properties.
    private static IEnumerable<Type> DocumentTypes()
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([typeof(CodeAltaConfigDocument)]);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!seen.Add(type)) continue;
            foreach (var property in Properties(type))
            {
                foreach (var reached in Reached(property.PropertyType))
                {
                    // The record of a provider of a newer version is immutable: it is shared, not copied.
                    if (reached.Namespace == typeof(CodeAltaConfigDocument).Namespace && reached.IsClass && reached != typeof(CodeAltaUnsupportedProvider))
                        pending.Push(reached);
                }
            }
        }

        return seen;

        static IEnumerable<Type> Reached(Type type)
            => type.IsGenericType ? type.GetGenericArguments().SelectMany(Reached).Append(type) : [type];
    }

    private static IEnumerable<PropertyInfo> Properties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(static property => property.CanWrite);

    private static void AssertEquivalent(object? original, object? copy, string path)
    {
        if (original is null || copy is null)
        {
            Assert.AreEqual(original is null, copy is null, $"{path}: null in one only.");
            return;
        }

        // A read-only list of records may come back as another list type; everything else keeps its type.
        if (original is not Array) Assert.AreEqual(original.GetType(), copy.GetType(), $"{path}: type");
        switch (original)
        {
            case string or ValueType or CodeAltaUnsupportedProvider:
                Assert.AreEqual(original, copy, path);
                return;
            case TomlTable table:
                Assert.AreNotSame(original, copy, $"{path}: shared table");
                var copiedTable = (TomlTable)copy;
                Assert.AreEqual(table.Kind, copiedTable.Kind, $"{path}: kind of table");
                CollectionAssert.AreEqual(table.Keys.ToArray(), copiedTable.Keys.ToArray(), $"{path}: keys");
                foreach (var key in table.Keys) AssertEquivalent(table[key], copiedTable[key], $"{path}.{key}");
                return;
            case IDictionary dictionary:
                Assert.AreNotSame(original, copy, $"{path}: shared dictionary");
                var copiedDictionary = (IDictionary)copy;
                Assert.AreSame(original.GetType().GetProperty("Comparer")!.GetValue(original), copy.GetType().GetProperty("Comparer")!.GetValue(copy), $"{path}: comparer");
                CollectionAssert.AreEqual(dictionary.Keys.Cast<object>().ToArray(), copiedDictionary.Keys.Cast<object>().ToArray(), $"{path}: keys in order");
                foreach (var key in dictionary.Keys) AssertEquivalent(dictionary[key], copiedDictionary[key], $"{path}[{key}]");
                return;
            case IEnumerable sequence:
                Assert.AreNotSame(original, copy, $"{path}: shared list");
                var items = sequence.Cast<object?>().ToArray();
                var copiedItems = ((IEnumerable)copy).Cast<object?>().ToArray();
                Assert.AreEqual(items.Length, copiedItems.Length, $"{path}: count");
                for (var index = 0; index < items.Length; index++) AssertEquivalent(items[index], copiedItems[index], $"{path}[{index}]");
                return;
        }

        Assert.AreNotSame(original, copy, $"{path}: shared object");
        foreach (var property in Properties(original.GetType()))
        {
            AssertEquivalent(property.GetValue(original), property.GetValue(copy), $"{path}.{property.Name}");
        }
    }

    // Gives every property a value of its own that is not its default, so that a property left out of the copy shows.
    private sealed class Filler
    {
        private int _next;

        public HashSet<Type> Filled { get; } = [];

        public object Fill(Type type, string path)
        {
            Filled.Add(type);
            var instance = Activator.CreateInstance(type)!;
            foreach (var property in Properties(type))
            {
                var value = Value(property.PropertyType, $"{path}.{property.Name}");
                property.SetValue(instance, value);
                Assert.AreNotEqual(property.GetValue(Activator.CreateInstance(type)), value, $"{path}.{property.Name} is filled with its default.");
            }

            return instance;
        }

        private object Value(Type type, string path)
        {
            var number = ++_next;
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying == typeof(string)) return $"{path}#{number}";
            if (underlying == typeof(bool)) return number % 2 == 0 ? false : true;
            if (underlying == typeof(int)) return number;
            if (underlying == typeof(long)) return (long)number * 1000;
            if (underlying == typeof(double)) return number / 7.0;
            if (type == typeof(List<string>)) return new List<string> { $"{path}#a{number}", $"{path}#b{number}" };
            if (type == typeof(List<int>)) return new List<int> { number, number + 1 };
            if (type == typeof(IReadOnlyList<CodeAltaUnsupportedProvider>)) return new[] { new CodeAltaUnsupportedProvider($"key{number}", "future") };
            if (type == typeof(TomlTable)) return Table(number);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
            {
                var dictionary = (IDictionary)Activator.CreateInstance(type, StringComparer.OrdinalIgnoreCase)!;
                var valueType = type.GetGenericArguments()[1];
                // Two keys, in an order that is not the sorted one.
                dictionary.Add($"Zeta{number}", Value(valueType, $"{path}[Zeta]"));
                dictionary.Add($"alpha{number}", Value(valueType, $"{path}[alpha]"));
                return dictionary;
            }

            if (type.IsClass && type.Namespace == typeof(CodeAltaConfigDocument).Namespace) return Fill(type, path);
            Assert.Fail($"{path}: the test does not know how to fill a {type}; teach it, and the copy, that type.");
            return null!;
        }

        private static TomlTable Table(int number)
        {
            var nested = new TomlTable(inline: true) { ["flag"] = true };
            var table = new TomlTable
            {
                ["text"] = $"value{number}",
                ["number"] = (long)number,
                ["nested"] = nested,
                ["empty"] = new TomlTable(),
                ["list"] = new TomlArray { 1L, "two", new TomlTable(inline: true) { ["three"] = 3L } },
            };
            var tables = new TomlTableArray { new TomlTable { ["item"] = 1L } };
            table["items"] = tables;
            return table;
        }
    }
}
