# Security Policy

Nova3DVisualiser is a small, solo open-source project (GPL-3.0). Security is handled on a
best-effort basis.

## Supported versions

Security fixes are made only on the latest release and the `main` branch.

| Version | Supported |
| ------- | --------- |
| 1.0.x (latest release / `main`) | ✅ |
| older or unreleased commits | ❌ |

## Security model and scope

Nova3DVisualiser's multiplayer is designed for **trusted peers** — a local network
or a group of friends. The wire protocol is **not authenticated and not
encrypted**: do not expose a server to the open internet, and do not connect to
untrusted servers. As defensive measures the server bounds resource use against
malformed input and floods (allocation, connection, queue, and per-peer rate
limits), and received meshes and textures are written to a sandboxed folder with
sanitized filenames. Received nicknames are length-capped and character-sanitized
on receipt. These measures raise the bar against accidental and
low-effort abuse; they are not a substitute for a trusted network.

## Reporting a vulnerability

Please report security issues **privately** using GitHub's **"Report a vulnerability"**
button on this repository's **Security** tab (GitHub Security Advisories). **Do not open a
public issue** for a security problem.

Where possible, include:

- what the vulnerability is and its impact,
- steps (or a proof of concept) to reproduce it,
- the affected version / commit, and your OS, terminal, and renderer (CPU or GPU).

## What to expect

As a solo project, responses are best-effort. You can expect an acknowledgement of your
report and, once an issue is confirmed, a fix landed on `main` and included in the next
release. There is no bug-bounty program.
