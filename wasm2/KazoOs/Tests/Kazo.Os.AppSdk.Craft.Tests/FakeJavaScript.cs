using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>
/// A JavaScript runtime that records what <see cref="CraftFrame"/> calls: <c>import</c> of the module,
/// then <c>create</c> and the methods of the frame object it returns.
/// </summary>
internal sealed class FakeJavaScript : IJSRuntime
{
    public FakeJavaScript()
    {
        Module = new FakeObject(this);
        Frame = new FakeObject(this);
    }

    public List<(string Identifier, object?[] Arguments)> Calls { get; } = [];

    public FakeObject Module { get; }

    public FakeObject Frame { get; }

    /// <summary>What <c>request</c> answers; an exception to throw instead.</summary>
    public Func<object?[], object?> RequestAnswer { get; set; } = _ => JsonDocument.Parse("""{"ok":1}""").RootElement.Clone();

    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Calls.Add(("runtime." + identifier, args ?? []));
        return ValueTask.FromResult((TValue)(object)Module);
    }

    internal sealed class FakeObject(FakeJavaScript owner) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object?[] arguments = args ?? [];
            owner.Calls.Add((identifier, arguments));
            object? result = identifier switch
            {
                "create" => owner.Frame,
                "request" => owner.RequestAnswer(arguments),
                "open" => true,
                "colorScheme" => "light",
                _ => null,
            };
            return result is Exception exception
                ? ValueTask.FromException<TValue>(exception)
                : ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
