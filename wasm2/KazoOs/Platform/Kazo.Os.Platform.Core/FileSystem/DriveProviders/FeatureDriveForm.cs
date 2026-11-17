using System.Globalization;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.FileSystem.DriveProviders;

/// <summary>
/// Checks the values of a form connection against its declaration (ADR 0282 §2, §3) before the OS
/// saves the account: whatever app sent them (the Explorer, <c>mount -o</c>, an app with
/// <c>cloud-accounts.manage</c>), a provider only ever receives the fields it declared, in the shape
/// it declared, with its secrets sealed.
/// </summary>
public static class FeatureDriveForm
{
    /// <summary>The longest value the OS keeps for one field.</summary>
    public const int MaxValueLength = 2048;

    /// <summary>Splits and checks the values of a form.</summary>
    /// <param name="type">The drive type, for the messages.</param>
    /// <param name="fields">The declared fields.</param>
    /// <param name="options">The non-secret values the caller sent; <see langword="null"/> for none.</param>
    /// <param name="secrets">The secret values the caller sent; <see langword="null"/> for none.</param>
    /// <returns>The non-secret and the secret values, by field name, without empty optional ones.</returns>
    /// <exception cref="ArgumentException">A value is unknown, missing, in the wrong half, or malformed; the message says which.</exception>
    public static (Dictionary<string, string> Options, Dictionary<string, string> Secrets) Check(
        string type,
        IReadOnlyList<AppDriveFormFieldManifest> fields,
        IReadOnlyDictionary<string, string>? options,
        IReadOnlyDictionary<string, string>? secrets)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Dictionary<string, AppDriveFormFieldManifest> byName = fields.ToDictionary(static field => field.Name, StringComparer.Ordinal);
        foreach (string name in (options?.Keys ?? []).Concat(secrets?.Keys ?? []))
        {
            if (!byName.ContainsKey(name))
            {
                throw new ArgumentException($"{type}: unknown field '{name}'.", nameof(options));
            }
        }

        Dictionary<string, string> keptOptions = new(StringComparer.Ordinal);
        Dictionary<string, string> keptSecrets = new(StringComparer.Ordinal);
        foreach (AppDriveFormFieldManifest field in fields)
        {
            // A secret must never travel in the clear half (it would be kept in the account list), and the
            // other way round a plain value has no reason to be sealed.
            IReadOnlyDictionary<string, string>? wrongHalf = field.Secret ? options : secrets;
            if (wrongHalf is not null && wrongHalf.ContainsKey(field.Name))
            {
                throw new ArgumentException(
                    field.Secret ? $"{type}: '{field.Name}' is a secret field." : $"{type}: '{field.Name}' is not a secret field.",
                    nameof(options));
            }

            IReadOnlyDictionary<string, string>? half = field.Secret ? secrets : options;
            string? value = half is not null && half.TryGetValue(field.Name, out string? given) ? given : null;
            if (!field.Secret)
            {
                value = value?.Trim();
            }

            if (string.IsNullOrEmpty(value))
            {
                if (field.Required)
                {
                    throw new ArgumentException($"{type}: '{field.Label}' is required.", nameof(options));
                }

                continue;
            }

            Validate(type, field, value);
            (field.Secret ? keptSecrets : keptOptions)[field.Name] = value;
        }

        return (keptOptions, keptSecrets);
    }

    /// <summary>
    /// What the account list shows for a form account: the host of the first address field, else the
    /// first non-secret value, else the service's name. Never a secret.
    /// </summary>
    /// <param name="fields">The declared fields.</param>
    /// <param name="options">The checked non-secret values.</param>
    /// <param name="serviceName">The declared name of the service.</param>
    public static string AccountName(IReadOnlyList<AppDriveFormFieldManifest> fields, IReadOnlyDictionary<string, string> options, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);
        string[] values = [.. fields.Where(static field => !field.Secret)
            .Select(field => (field, value: options.GetValueOrDefault(field.Name)))
            .Where(static pair => !string.IsNullOrEmpty(pair.value))
            .Select(static pair => pair.field.Kind == AppDriveFormFieldKind.Url && Uri.TryCreate(pair.value, UriKind.Absolute, out Uri? uri)
                ? uri.Host
                : pair.value!)];
        return values.Length switch
        {
            0 => serviceName,
            1 => values[0],
            _ => $"{values[0]} ({values[1]})"
        };
    }

    private static void Validate(string type, AppDriveFormFieldManifest field, string value)
    {
        if (value.Length > MaxValueLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{type}: '{field.Label}' is not a valid value.", nameof(value));
        }

        bool valid = field.Kind switch
        {
            // ADR 0282 §2: a form address is https, like every address a provider reaches.
            AppDriveFormFieldKind.Url => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host),
            AppDriveFormFieldKind.Number => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            AppDriveFormFieldKind.Choice => field.Choices?.Contains(value, StringComparer.Ordinal) == true,
            _ => true
        };
        if (!valid)
        {
            throw new ArgumentException(
                field.Kind == AppDriveFormFieldKind.Url ? $"{type}: '{field.Label}' must be an https address." : $"{type}: '{field.Label}' is not a valid value.",
                nameof(value));
        }
    }
}
