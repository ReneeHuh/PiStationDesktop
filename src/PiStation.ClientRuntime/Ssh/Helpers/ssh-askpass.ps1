# The password is supplied only to this SSH process tree, never in arguments or files.
if ($null -ne $env:PISTATION_SSH_AUTH_SECRET) {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::Out.WriteLine($env:PISTATION_SSH_AUTH_SECRET)
    exit 0
}
exit 1
