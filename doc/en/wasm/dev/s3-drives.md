> [Version française](../../../fr/wasm/dev/s3-drives.md)

# S3 drives and the bucket's CORS rule

Work item `cloud-drives/6-s3` of the
[`cloud-drives/base`](plans/cloud-drives-plan.md) series, following
[ADR 0267](adr/0267-cloud-drives-as-network-drives.md), sections 3, 4 and 6.
The connector builds on the [drive sources](network-drive-sources.md) (work
item 1) and the [cloud accounts](cloud-accounts.md) (work item 2).

## Purpose

A bucket of an S3 compatible service (Cloudflare R2, Backblaze B2, MinIO, AWS
S3), or a prefix of it, mounts as a network drive under `/mnt/<bucket>`. No app
to register and no Kazo OS server: the user gives the address of the service,
their keys and the bucket; requests are signed in the OS (SigV4) and go from the
browser to the service. The only thing to set up at the provider is a **CORS
rule** on the bucket (see below).

## Architecture

Everything is in `Platform/Kazo.Os.Platform.Core/CloudDrives/S3/`, with no
browser dependency: `HttpClient` becomes `fetch` on WebAssembly.

| Piece | Role |
| --- | --- |
| `S3SignatureV4` | AWS Signature Version 4 (`Authorization` header), encoding of paths and queries. SHA-256 and HMAC-SHA-256 in .NET, the same in the browser and in tests. |
| `S3Connection` | Address, region, addressing (path or virtual host), access key and secret key. Checks what the user typed; never prints the secret. |
| `S3Client` | `ListObjectsV2`, `HEAD`, `GET`, `PUT` (with `If-Match` or `If-None-Match: *`), `DELETE`, `CopyObject`. XML answers are read without an XML library, to keep the download light. Every failure becomes a `HostFolderException`. |
| `S3Folder` | `IHostFolder` and `IConditionalHostFolder` of a bucket or a prefix. |
| `S3DriveSource` | The `s3` source (`INetworkDriveSource`), registered in the Ecosystem host. |

### Simulated folders

S3 only has keys. A folder is a common prefix: `2026/beach.jpg` shows a folder
`2026`.

- Creating a folder writes an empty object `2026/` (the marker of the S3
  consoles).
- Deleting the last file of a folder writes the parent folder's marker, so the
  folder does not vanish.
- Deleting a folder deletes every key of its prefix, deepest first and the
  marker last; refused (`DirectoryNotEmpty`) without `recursive` when it holds
  anything.
- Renaming or moving a file: `CopyObject` on the service then `DELETE`. A
  folder moves file by file (the OS already does it when `TryMoveAsync` says
  no).
- A key `a` next to keys `a/…`: the folder wins, Kazo OS cannot show both.

### Revision and conditional write

A file's revision is its `ETag`. A write that expects a revision sends
`If-Match: <ETag>`; the service's refusal (`412`, or `409
ConditionalRequestConflict`) becomes the OS's revision conflict. Creating a file
sends `If-None-Match: *`. A service that ignores these headers still writes: the
check made first in the OS stays.

No quota: S3 has none, `df` shows `-`.

### Where the keys go

The service and the keys make a **cloud account** of provider `s3`
(`CloudAccountService`), like a Dropbox or OneDrive account:

- the secret key is sealed with the address, the region and the access key, on
  the device, under a key of the keyring
  ([ADR 0269](adr/0269-cloud-tokens-stay-on-the-device.md)); the account line
  has `"credential": "keys"`;
- the account shows as "`<access key> — <host>`"; the same access key on the
  same service updates that account instead of adding one;
- the drive list keeps only the account reference and `bucket/prefix` as root;
- removing the account unmounts its drives and erases its key from the keyring;
- the guest, who has no keyring, cannot mount an S3 drive;
- `GetAccessTokenAsync` and `ReconnectAsync` refuse a keys account: it is
  reconnected by typing its keys again;
- S3 is not in `CloudAccountService.GetProvidersAsync` (no app id): the UI asks
  `NetworkDriveService.IsAvailableAsync("s3")`, always true.

## Usage

```csharp
NetworkDrive? drive = await drives.MountAsync("s3", null, new NetworkDriveRequest
{
    Options = new Dictionary<string, string>
    {
        ["endpoint"] = "https://<id>.r2.cloudflarestorage.com",
        ["region"] = "auto",
        ["bucket"] = "photos",
        ["prefix"] = "family/2026",    // optional
        ["addressing"] = "path"        // path | virtual; absent: virtual for *.amazonaws.com, path otherwise
    },
    Secrets = new Dictionary<string, string>
    {
        ["access-key-id"] = "…",
        ["secret-access-key"] = "…"
    }
});

// Another bucket with the keys of an existing s3 account:
await drives.MountAsync("s3", null, new NetworkDriveRequest(accountId)
{
    Options = new Dictionary<string, string> { ["bucket"] = "archives" }
});
```

- `endpoint`: `https://host[:port]`, no path; `http` only for `localhost` (a
  MinIO on the user's computer), since an `https` page cannot call `http`.
- Empty `region`: `us-east-1`. Cloudflare R2: `auto`.
- Default mount point: `/mnt/<bucket>`, then `-2`, `-3`…
- Before mounting, the source lists the bucket's first key. A refusal becomes
  `NetworkDriveError.InvalidRequest` with a message that says what to fix: keys
  refused (or wrong region), no such bucket, no right to list, service
  unreachable (network, address or **CORS rule**).

### Login and network

- At the user's login the drive is opened again with no request: the login
  never waits for the network.
- A request that fails (offline, service down, `5xx`) disconnects the drive
  (`ProviderUnavailable`). The source tries again every 30 seconds and when the
  page is shown again; the drive comes back by itself once the service answers.
- Keys the service no longer accepts (`InvalidAccessKeyId`,
  `SignatureDoesNotMatch`) disconnect the drive too, which waits for new keys:
  mounting the same bucket with the new keys updates the account and reconnects
  the drive.
- The browser's HTTP cache is bypassed (`cache: no-store`): the drive always
  sees the service as it is.
- An object larger than 128 MiB shows in the listing; reading it fails with
  `FileTooLarge` ([ADR 0245](adr/0245-raise-the-maximum-file-size.md)).

## The CORS rule to put on the bucket

The browser sends a request from Kazo OS to the service only when the bucket
allows it. Without a rule, mounting fails with "cannot be reached … CORS rule".
The rule must:

- allow the origin of Kazo OS: `https://os.kazo.ca` (add
  `http://localhost:5000` or the development port when needed);
- allow the methods `GET`, `HEAD`, `PUT`, `DELETE`;
- allow the headers `authorization`, `content-type`, `x-amz-content-sha256`,
  `x-amz-date`, `x-amz-copy-source`, `if-match`, `if-none-match` (or `*` where
  the service accepts it);
- **expose the `ETag` header**: without it, Kazo OS cannot see the service's
  revision and cannot write on condition.

Advice: create a key for Kazo OS limited to this bucket, read and write, rather
than a key for the whole account.

### Cloudflare R2

Dashboard → R2 → the bucket → **Settings** → **CORS Policy** → **Add CORS
policy**, in JSON:

```json
[
  {
    "AllowedOrigins": ["https://os.kazo.ca"],
    "AllowedMethods": ["GET", "HEAD", "PUT", "DELETE"],
    "AllowedHeaders": ["authorization", "content-type", "x-amz-content-sha256", "x-amz-date", "x-amz-copy-source", "if-match", "if-none-match"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }
]
```

In Kazo OS: address `https://<account id>.r2.cloudflarestorage.com`, region
`auto`, addressing `path`. Keys come from **R2 → Manage R2 API Tokens**
("Object Read & Write", limited to the bucket).

### Backblaze B2

The B2 web UI only offers ready-made rules; the full rule is set with the `b2`
tool:

```sh
b2 bucket update --cors-rules '[
  {
    "corsRuleName": "kazoos",
    "allowedOrigins": ["https://os.kazo.ca"],
    "allowedOperations": ["s3_get", "s3_head", "s3_put", "s3_delete"],
    "allowedHeaders": ["authorization", "content-type", "x-amz-content-sha256", "x-amz-date", "x-amz-copy-source", "if-match", "if-none-match"],
    "exposeHeaders": ["ETag"],
    "maxAgeSeconds": 3600
  }
]' <bucket> allPrivate
```

In Kazo OS: address `https://s3.<region>.backblazeb2.com` (for example
`s3.us-west-004.backblazeb2.com`), region `us-west-004`, addressing `path`. An
application key limited to the bucket (**Application Keys**).

### MinIO

MinIO answers CORS requests from every origin by default. To limit it to Kazo
OS, start the server with:

```sh
MINIO_API_CORS_ALLOW_ORIGIN=https://os.kazo.ca minio server /data
```

In Kazo OS: the server's address (`http://localhost:9000` is accepted for a
MinIO on the computer), region `us-east-1` unless configured otherwise,
addressing `path`. If the revision does not show, check that the version in use
exposes the `ETag` header.

### AWS S3

Console → S3 → the bucket → **Permissions** → **Cross-origin resource sharing
(CORS)** → **Edit**, the same JSON rule as for R2. Or on the command line:

```sh
aws s3api put-bucket-cors --bucket <bucket> --cors-configuration '{
  "CORSRules": [{
    "AllowedOrigins": ["https://os.kazo.ca"],
    "AllowedMethods": ["GET", "HEAD", "PUT", "DELETE"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }]
}'
```

In Kazo OS: address `https://s3.<region>.amazonaws.com` (for example
`s3.eu-west-3.amazonaws.com`), the bucket's region, addressing left empty
(virtual host). A bucket whose name has dots needs `path` addressing: its
certificate does not cover `a.b.s3.…`.

## Tests

- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/S3/S3SignatureV4Tests.cs`: the
  four signature examples of AWS's public documentation (GET with `Range`,
  `?lifecycle`, listing with `max-keys` and `prefix`, PUT with a body),
  encoding, path and virtual-host addresses, refused addresses, secret never
  printed.
- `Tests/Kazo.Os.Platform.Core.Tests/CloudDrives/S3/S3DriveSourceTests.cs`,
  against `FakeS3Server` (an in-memory S3 service that **checks the signature of
  every request**): mount with an `s3` account and the secret absent from both
  lists, folders from prefixes and markers, rename by copy, `ETag` and
  `If-Match`, the service's refusal as a revision conflict, a prefix and a second
  bucket with the same account, virtual host, refused mounts with their reason,
  invalid values refused before any request, guest refused, network lost and
  back, login with no request, keys refused then typed again, unreadable keys,
  account removal, a service without conditional writes, multi-page listings,
  an object over 128 MiB.
- Not done: a trial against a real local MinIO (downloading MinIO was not
  allowed in the development container). Real trials are grouped in work
  item 7.

## Decisions

- **An `s3` cloud account rather than a separate key store**: the accounts page
  (work item 5) lists S3 with the others, and "Remove" unmounts its drives.
- **One account per access key and service, one drive per bucket**: the same
  key serves several buckets without being typed again.
- **No XML library**: the three useful XML answers read in a few lines;
  `System.Private.Xml` would weigh on the OS download.
- **The login does not reach the service**; after a network failure, the drive
  asks the service before it calls itself reconnected.
- **`HostFolderErrorKind.FileTooLarge` (value 10)**, shared with the OneDrive
  connector (work item 4), becomes `FileSystemErrorCode.FileTooLarge`.

## Tasks

- [x] SigV4 signature checked against the published examples.
- [x] S3 client: listing, `HEAD`, `GET`, conditional `PUT`, `DELETE`, copy.
- [x] Folders simulated by prefixes, markers.
- [x] `ETag` revision and `If-Match`.
- [x] Keys sealed in an `s3` cloud account.
- [x] `s3` source, registered in the Ecosystem host.
- [x] Network failure, coming back alone, keys refused.
- [x] CORS rule guide (R2, B2, MinIO, AWS).
- [x] Tests against a fake S3 service that checks signatures.
- [ ] Trial against a real service (work item 7).
