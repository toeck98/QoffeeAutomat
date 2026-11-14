# Prototype Reference Material

This directory contains reference files preserved from the `prototype-v0` tag for task `T309` (*Port the prototype features as command templates*):

- [`modes.json`](./modes.json): The original automation mode definitions (display configuration profiles paired with sequential program launches and sleep delays).
- [`DisplayModule.cs`](./DisplayModule.cs): The original PowerShell `DisplayConfig` module execution and profile path resolution logic (`Import-Clixml ... | Use-DisplayConfig`, `-UpdateAdapterIds`, and `Set-DisplayConfig` fallback).

The full original prototype remains reachable in Git via the `prototype-v0` tag.
