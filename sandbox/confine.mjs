// Runs Claude's Lima VM (D81) so that its only way out is srt's allowlisting proxy: Lima's host process, which
// re-creates every connection the VM makes, may connect only to that proxy and to the VM's SSH port (macOS
// Seatbelt, enforced by the host's kernel; root in the VM can't lift it). Also keeps Claude's tokens (GitHub's,
// and Azure's in the platform profile) in the VM fresh (sandbox/tokens). Started by sandbox/sandbox, and lives as
// long as the VM: `limactl stop` ends both.
//
//   node confine.mjs <instance>
import { SandboxManager } from '@anthropic-ai/sandbox-runtime'
import { execFileSync, spawn } from 'node:child_process'
import { readFileSync, watch } from 'node:fs'
import { createServer } from 'node:net'

const [instance] = process.argv.slice(2)
const here = new URL('.', import.meta.url).pathname
const allowlist = new URL('allowlist.json', import.meta.url)
const allowed = () => JSON.parse(readFileSync(allowlist, 'utf8'))

await SandboxManager.initialize({
  network: {
    allowedDomains: allowed(),
    deniedDomains: [],
    // Allowed names must not lead to the LAN either (loopback and the host's own addresses are denied already).
    deniedResolvedAddresses: ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '100.64.0.0/10', 'fc00::/7'],
  },
  filesystem: { denyRead: [], allowWrite: [] },
}, async ({ host, port }) => {
  console.error(`refused ${host}:${port} (not in sandbox/allowlist.json)`)
  return false
})
// Edits to the allowlist apply at once; a half-written file keeps the previous list.
watch(allowlist, () => {
  try {
    const config = SandboxManager.getConfig()
    SandboxManager.updateConfig({ ...config, network: { ...config.network, allowedDomains: allowed() } })
    console.error('allowlist reloaded')
  } catch (error) {
    console.error(`allowlist not reloaded: ${error.message}`)
  }
})

const freePort = () => new Promise(resolve => {
  const server = createServer().listen(0, '127.0.0.1', () => {
    const { port } = server.address()
    server.close(() => resolve(port))
  })
})
const sshPort = await freePort()
const proxyPort = SandboxManager.getProxyPort()
const proxy = `http://srt:${SandboxManager.getProxyAuthToken()}@localhost:${proxyPort}`
const profile = `(version 1)
(allow default)
(deny network-outbound (remote ip "*:*"))
(allow network-outbound (remote ip "localhost:${proxyPort}"))
(allow network-outbound (remote ip "localhost:${sshPort}"))`

// Lima passes these into the VM, with localhost as the host's address there. Not through the proxy: the VM
// itself, and the private ranges its Docker, kind and the cluster use.
const noProxy = 'localhost,127.0.0.1,::1,.svc,.cluster.local,.local,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16'
const env = {
  ...process.env,
  HTTP_PROXY: proxy, HTTPS_PROXY: proxy, NO_PROXY: noProxy,
  http_proxy: proxy, https_proxy: proxy, no_proxy: noProxy,
}
const lima = spawn('sandbox-exec', ['-p', profile, 'limactl', 'start', '--foreground', '--tty=false',
  '--ssh-port', String(sshPort), instance], { stdio: 'inherit', env })
lima.on('exit', code => process.exit(code ?? 1))

// Claude's tokens (GitHub's, and Azure's in the platform profile) last an hour: fresh ones every 45 minutes.
const renewTokens = () => {
  try {
    execFileSync(`${here}tokens`, [instance], { stdio: ['ignore', 'ignore', 'pipe'] })
  } catch (error) {
    console.error(`Tokens not renewed: ${error.message}`)
  }
}
setInterval(renewTokens, 45 * 60 * 1000)
