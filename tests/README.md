# Protocol / TLS tests

`SunshineWindowFollow.ProtocolTests` is a standalone .NET 8 console test runner. It links the
pure protocol sources from `../../src` and does **not** require Playnite to be installed. The
plugin's `SunshineWindowFollow.csproj` excludes `tests/**/*.cs`, so these files are not compiled
into `SunshineWindowFollow.dll`.

```powershell
dotnet run --project .\tests\SunshineWindowFollow.ProtocolTests -c Release
```

The runner starts local Kestrel HTTP/HTTPS servers. The HTTPS certificate is generated at
runtime and is deliberately self-signed with `CN=localhost` while clients connect to
`127.0.0.1`, so certificate-chain and name-mismatch errors are both exercised.

Coverage:

- nested `{ok,sessions:[...],selected_session_id,selected_client_uuid}` parsing;
- `Active = override.enabled` (not `observed.active`);
- `Mode` / `Window` / `Fallback` / `FallbackToDisplay` mapping;
- session selection by `selected_session_id`, `selected_client_uuid`, selected marker, or the
  only session, while retaining the full `sessions` list;
- legacy root-level `active` / `mode` / `window` / `fallback` fields are ignored;
- request JSON contains `session_id` without protocol-extra fields (including UTF-8/quote escaping);
- only loopback endpoints are accepted; URL userinfo and plaintext HTTP credentials are rejected,
  and a forged non-loopback endpoint is refused before any request is sent;
- self-signed loopback HTTPS works without changing
  `ServicePointManager.ServerCertificateValidationCallback`;
- certificate errors are accepted only for the exact loopback request URI; `requestUri == null`
  is rejected;
- redirects are not followed;
- self-signed TLS failures produce an actionable error message;
- malformed JSON returns `null`.

The project also includes small stubs for `Playnite.SDK.ILogger` and `SunshineSettings` so the
linked sources compile without the Playnite SDK. They live only in this test project.
