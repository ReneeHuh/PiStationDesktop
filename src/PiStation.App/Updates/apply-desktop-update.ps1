param([Parameter(Mandatory = $true)][string] $HandoffPath)
$ErrorActionPreference = 'Stop'
$handoff = Get-Content -LiteralPath $HandoffPath -Raw | ConvertFrom-Json
$update = $handoff.update
$receiptPath = [IO.Path]::GetFullPath($update.receiptPath)
$stageRoot = [IO.Path]::GetFullPath((Join-Path $update.dataRoot ('remote-updates\' + ([guid]$update.requestId).ToString('N'))))
if ($receiptPath -ne (Join-Path $stageRoot 'receipt.json') -or
    [IO.Path]::GetFullPath($update.packagePath) -ne (Join-Path $stageRoot 'package.msix')) {
    throw 'The update handoff is outside its staging directory.'
}
function Save-Result([string] $State, [string] $Message) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $receipt.receipt.state = if ($State -eq 'Succeeded') { 4 } else { 5 }
    $receipt.receipt.message = $Message
    $receipt.receipt.updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $temporary = $receiptPath + '.owner.tmp'
    [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $receiptPath -Force
}
try {
    $parent = Get-Process -Id $handoff.parentId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $handoff.parentStartedTicks) {
        if (-not $parent.WaitForExit(60000)) { throw 'The owning desktop did not close.' }
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath $update.packagePath -Algorithm SHA256).Hash
    if ($hash -ne $receipt.request.sha256) { throw 'The staged package changed.' }
    # Windows enforces the signature/trust policy again during registration.
    Add-AppxPackage -Path $update.packagePath -ErrorAction Stop
    $installed = Get-AppxPackage | Where-Object { $_.Name -eq $handoff.packageName -and $_.Publisher -eq $handoff.publisher } | Select-Object -First 1
    if (-not $installed -or [version]$installed.Version -ne [version]$update.targetVersion) { throw 'Windows did not activate the expected package version.' }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPiActivationManager {
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}
public static class PiPackageActivation {
    public static void Start(string app, string arguments) {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
        var manager = (IPiActivationManager)Activator.CreateInstance(type);
        uint id; Marshal.ThrowExceptionForHR(manager.ActivateApplication(app, arguments, 0, out id));
        Marshal.ReleaseComObject(manager);
    }
    public static void VerifyHost(int port, string credential, string fingerprint) {
        var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://127.0.0.1:" + port + "/ssh/health");
        request.AllowAutoRedirect = false;
        request.Timeout = 5000;
        request.Headers["Authorization"] = "Bearer " + credential;
        request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => {
            if (certificate == null) return false;
            using (var parsed = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate))
            using (var hash = System.Security.Cryptography.SHA256.Create()) {
                return parsed.NotBefore.ToUniversalTime() <= DateTime.UtcNow && parsed.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                    BitConverter.ToString(hash.ComputeHash(parsed.RawData)).Replace("-", "").Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
            }
        };
        using (var response = (System.Net.HttpWebResponse)request.GetResponse())
            if (response.StatusCode != System.Net.HttpStatusCode.OK) throw new InvalidOperationException("Host readiness failed.");
    }
}
'@
    $arguments = '--data-root "' + $update.dataRoot.Replace('"', '\"') + '"'
    [PiPackageActivation]::Start($handoff.appUserModelId, $arguments)
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $handoff.discoveryPipe, [IO.Pipes.PipeDirection]::In)
        try {
            $pipe.Connect(500)
            $reader = [IO.StreamReader]::new($pipe)
            $line = $reader.ReadLineAsync()
            if (-not $line.Wait(3000)) { throw 'Host identity discovery timed out.' }
            $info = $line.Result | ConvertFrom-Json
            if ($info.environmentId -ne $handoff.environmentId -or $info.certificateFingerprint -ne $handoff.certificateFingerprint) {
                throw 'The restarted desktop changed its environment identity.'
            }
            [PiPackageActivation]::VerifyHost($info.port, $info.bearerCredential, $handoff.certificateFingerprint)
            $ready = $true
            break
        } catch [TimeoutException] { } finally { $pipe.Dispose() }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The new desktop did not report its environment identity.' }
    Save-Result 'Succeeded' 'Windows installed the trusted package and the owner verified the restarted environment identity.'
} catch {
    Save-Result 'Failed' 'Desktop update or restart verification failed. The package owner must inspect deployment status before retrying.'
    exit 1
}
