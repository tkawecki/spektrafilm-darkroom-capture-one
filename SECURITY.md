# Security policy

## Supported versions

Only the latest release is supported, and only with Capture One **16.9.0.3894** for Windows. Older releases do not get fixes.

## What counts as a security problem

The panel loads into Capture One through a .NET startup hook and runs native code (the Vulkan renderer). Please report privately anything that could let someone else run code, read or change files, or reach the network through the panel, for example:

- the launcher, installer or startup hook loading a DLL or script from a place other users can write to,
- saved looks, presets, LUTs or other files the panel reads leading to code execution or writes outside their folders,
- the panel sending data over the network (it is designed to send nothing).

Ordinary bugs, crashes and wrong colours are not security problems; please open a normal issue for them.

## How to report

Use **Report a vulnerability** on the repository's **Security** tab. This opens a private report that only the maintainer can see. Do not open a public issue for a security problem.

Please include the release version, your Capture One version, what you did and what happened, and if possible a minimal file or set of steps that reproduces it.

This is a project maintained in spare time; there is no fixed response time, but reports are read and confirmed problems are fixed in the next release, with credit if you want it.
