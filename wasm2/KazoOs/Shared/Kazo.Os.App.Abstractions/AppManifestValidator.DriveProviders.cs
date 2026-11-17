namespace Kazo.Os.App.Abstractions;

/// <summary>The checks of ADR 0282 §1, §3 and §4 on <see cref="AppManifest.DriveProviders"/>.</summary>
public static partial class AppManifestValidator
{
    /// <summary>
    /// ADR 0282 on one manifest: only a feature provides drive types and holds <c>drives.provide</c>
    /// (<c>manifest.feature.only</c>); a feature that declares a provider holds the capability; each
    /// type has the form of a filesystem type, is not reserved and is declared once; the class is
    /// written <c>Full.Type.Name, Assembly</c>; the connection is complete; the hosts are names
    /// reached over <c>https</c>, never a bare wildcard. Whether the class derives from
    /// <c>KazoDriveProvider</c> is checked by the manifest generator, which loads the assembly;
    /// whether another feature already provides the type is checked at installation.
    /// </summary>
    private static void ValidateDriveProviders(AppManifest manifest, List<ManifestValidationError> errors)
    {
        bool holdsCapability = (manifest.Capabilities ?? []).Contains(AppCapabilities.DrivesProvide, StringComparer.Ordinal);
        if (manifest.Kind != AppKind.Feature)
        {
            if (manifest.DriveProviders is { Count: > 0 })
            {
                AddError(errors, "manifest.feature.only", "driveProviders", "Only a feature provides a type of network drive (ADR 0282 §1).");
            }

            if (holdsCapability)
            {
                AddError(errors, "manifest.feature.only", "capabilities", $"Only a feature declares '{AppCapabilities.DrivesProvide}' (ADR 0282).");
            }

            return;
        }

        if (manifest.DriveProviders is not { Count: > 0 } providers)
        {
            return;
        }

        if (!holdsCapability)
        {
            AddError(errors, "manifest.drive-provider.capability", "capabilities", $"A feature that provides a type of network drive declares '{AppCapabilities.DrivesProvide}' (ADR 0282).");
        }

        HashSet<string> assetPaths = new((manifest.Assets ?? []).Select(static asset => asset.Path), StringComparer.Ordinal);
        HashSet<string> types = new(StringComparer.Ordinal);
        foreach (AppDriveProviderManifest provider in providers)
        {
            if (provider is null)
            {
                AddError(errors, "manifest.drive-provider.type", "driveProviders", "A drive provider entry is empty.");
                continue;
            }

            string path = $"driveProviders[{provider.Type}]";
            ValidateDriveProviderIdentity(provider, path, types, errors);
            if (provider.Icon is { } icon)
            {
                ValidatePackageRelativePath(icon, $"{path}.icon", errors);
                if (!assetPaths.Contains(icon))
                {
                    AddError(errors, "manifest.drive-provider.icon", $"{path}.icon", $"Icon asset '{icon}' is not declared in assets.");
                }
            }

            foreach (string host in provider.Hosts ?? [])
            {
                if (!AppDriveProviders.IsValidHost(host))
                {
                    AddError(errors, "manifest.drive-provider.host", $"{path}.hosts",
                        $"'{host}' is not a host a provider may declare: a lowercase name such as api.example.com or *.example.com, without scheme, port or path; never '*' alone (ADR 0282 §4).");
                }
            }

            ValidateDriveConnection(provider.Connection, $"{path}.connection", errors);
        }
    }

    /// <summary>The type (form, reserved, duplicate), the name and the class of one provider.</summary>
    private static void ValidateDriveProviderIdentity(AppDriveProviderManifest provider, string path, HashSet<string> types, List<ManifestValidationError> errors)
    {
        if (!AppDriveProviders.IsValidType(provider.Type))
        {
            AddError(errors, "manifest.drive-provider.type", $"{path}.type",
                $"'{provider.Type}' is not a filesystem type: lowercase letters, digits and dashes, starting with a letter, at most {AppDriveProviders.MaxTypeLength} characters.");
        }
        else if (AppDriveProviders.IsReservedType(provider.Type))
        {
            AddError(errors, "manifest.drive-provider.reserved", $"{path}.type", $"'{provider.Type}' is a type of the OS: a feature cannot provide it (ADR 0282 §1).");
        }
        else if (!types.Add(provider.Type))
        {
            AddError(errors, "manifest.drive-provider.duplicate", $"{path}.type", $"The type '{provider.Type}' is declared twice.");
        }

        if (string.IsNullOrWhiteSpace(provider.Name))
        {
            AddError(errors, "manifest.drive-provider.name", $"{path}.name", "A drive provider has a name the user reads.");
        }

        if (!AppProvidedServiceManifest.TryParseName(provider.Provider, out _, out _))
        {
            AddError(errors, "manifest.drive-provider.class", $"{path}.provider", $"Write the provider's class as 'Full.Type.Name, Assembly' ('{provider.Provider}').");
        }
    }

    /// <summary>ADR 0282 §3: an OAuth connection has https addresses and at least one app id; a form has valid, unique fields.</summary>
    private static void ValidateDriveConnection(AppDriveConnectionManifest? connection, string path, List<ManifestValidationError> errors)
    {
        switch (connection)
        {
            case null:
                AddError(errors, "manifest.drive-provider.connection", path, "A drive provider declares how the user connects: 'oauth' or 'form'.");
                return;
            case { Kind: AppDriveConnectionKind.OAuth }:
                if ((connection.Fields ?? []).Count > 0)
                {
                    AddError(errors, "manifest.drive-provider.connection", $"{path}.fields", "An OAuth connection has no form fields.");
                }

                ValidateDriveOAuth(connection.OAuth, $"{path}.oauth", errors);
                return;
            case { Kind: AppDriveConnectionKind.Form }:
                if (connection.OAuth is not null)
                {
                    AddError(errors, "manifest.drive-provider.connection", $"{path}.oauth", "A form connection has no OAuth addresses.");
                }

                ValidateDriveFormFields(connection.Fields ?? [], $"{path}.fields", errors);
                return;
            default:
                AddError(errors, "manifest.drive-provider.connection", $"{path}.kind", "The connection kind is 'oauth' or 'form'.");
                return;
        }
    }

    private static void ValidateDriveOAuth(AppDriveOAuthManifest? oauth, string path, List<ManifestValidationError> errors)
    {
        if (oauth is null)
        {
            AddError(errors, "manifest.drive-provider.oauth", path, "An OAuth connection declares its addresses and app ids.");
            return;
        }

        RequireHttps(oauth.AuthorizationEndpoint, $"{path}.authorizationEndpoint", errors);
        RequireHttps(oauth.TokenEndpoint, $"{path}.tokenEndpoint", errors);
        if (oauth.RevocationEndpoint is { } revocation)
        {
            RequireHttps(revocation, $"{path}.revocationEndpoint", errors);
        }

        if (oauth.Identity is { } identity)
        {
            RequireHttps(identity.Address, $"{path}.identity.address", errors);
            if (string.IsNullOrWhiteSpace(identity.IdPath))
            {
                AddError(errors, "manifest.drive-provider.oauth", $"{path}.identity.idPath", "The identity address names the JSON path of the account id.");
            }
        }

        if (oauth.FullAccess is null && oauth.AppFolder is null)
        {
            AddError(errors, "manifest.drive-provider.oauth", path, "An OAuth connection offers at least one access: 'fullAccess' or 'appFolder'.");
        }

        foreach ((string name, AppDriveOAuthClientManifest? client) in new[] { ("fullAccess", oauth.FullAccess), ("appFolder", oauth.AppFolder) })
        {
            if (client is not null && string.IsNullOrWhiteSpace(client.ClientId))
            {
                AddError(errors, "manifest.drive-provider.oauth", $"{path}.{name}.clientId", "An access names the public app id registered with the service.");
            }
        }
    }

    private static void ValidateDriveFormFields(IReadOnlyList<AppDriveFormFieldManifest> fields, string path, List<ManifestValidationError> errors)
    {
        if (fields.Count == 0)
        {
            AddError(errors, "manifest.drive-provider.form", path, "A form connection declares at least one field.");
            return;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        int serverAddresses = 0;
        foreach (AppDriveFormFieldManifest field in fields)
        {
            if (field is null || !AppDriveProviders.IsValidFieldName(field.Name))
            {
                AddError(errors, "manifest.drive-provider.form", path, $"'{field?.Name}' is not a field name: lowercase letters, digits and dashes.");
                continue;
            }

            string at = $"{path}[{field.Name}]";
            if (!names.Add(field.Name))
            {
                AddError(errors, "manifest.drive-provider.form", at, $"The field '{field.Name}' is declared twice.");
            }

            if (string.IsNullOrWhiteSpace(field.Label))
            {
                AddError(errors, "manifest.drive-provider.form", $"{at}.label", "A field has a label the user reads.");
            }

            if (!Enum.IsDefined(field.Kind))
            {
                AddError(errors, "manifest.drive-provider.form", $"{at}.kind", "The field kind is 'text', 'url', 'number' or 'choice'.");
            }

            if (field.Secret && field.Default is not null)
            {
                // ADR 0269: a secret is typed by the user and sealed; a default would sit in clear in the manifest.
                AddError(errors, "manifest.drive-provider.form", $"{at}.default", "A secret field has no default value.");
            }

            if (field.ServerAddress)
            {
                ValidateServerAddressField(field, at, ++serverAddresses, errors);
            }

            bool hasChoices = field.Choices is { Count: > 0 };
            if (field.Kind == AppDriveFormFieldKind.Choice && !hasChoices)
            {
                AddError(errors, "manifest.drive-provider.form", $"{at}.choices", "A choice field lists its values.");
            }
            else if (field.Kind != AppDriveFormFieldKind.Choice && field.Choices is not null)
            {
                AddError(errors, "manifest.drive-provider.form", $"{at}.choices", "Only a choice field lists values.");
            }
        }
    }

    /// <summary>
    /// ADR 0284 §2: the server address is a required, non-secret url field, one per provider, since
    /// the OS allows the host typed there to the provider and shows it in the connection dialog.
    /// </summary>
    private static void ValidateServerAddressField(AppDriveFormFieldManifest field, string at, int count, List<ManifestValidationError> errors)
    {
        if (field.Kind != AppDriveFormFieldKind.Url || field.Secret || !field.Required)
        {
            AddError(errors, "manifest.drive-provider.server-address", $"{at}.serverAddress",
                "A server address field is a required url field that is not secret (ADR 0284 §2).");
        }

        if (count > 1)
        {
            AddError(errors, "manifest.drive-provider.server-address", $"{at}.serverAddress",
                "A provider marks one field at most as its server address (ADR 0284 §2).");
        }
    }

    private static void RequireHttps(string? address, string path, List<ManifestValidationError> errors)
    {
        if (!AppDriveProviders.IsValidHttpsAddress(address))
        {
            AddError(errors, "manifest.drive-provider.address", path, $"'{address}' is not an absolute https address.");
        }
    }
}
