namespace Kazo.Os.Tests.Support;

/// <summary>
/// The contract of a feature's service in the tests of ADR 0262: an interface of an assembly other
/// than the feature's, as a shared library of contracts is (ADR 0261).
/// </summary>
public interface IFixtureMachine
{
    /// <summary>Gets a text the provider gives, with the app that called.</summary>
    string Describe();

    /// <summary>An operation that needs a permission the caller may lack (ADR 0246).</summary>
    void Start();
}
