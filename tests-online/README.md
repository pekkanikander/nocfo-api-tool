# Online Regression Tests

This directory contains the shell-based online regression suite for the `nocfo` CLI.

The suite is intentionally separate from the offline `dotnet test` path. It runs the
existing CLI end to end against `https://api-tst.nocfo.io` using a repo-local profile
selected through `NOCFO_TOOL_CONFIG_HOME`.

## Setup

1. Copy `tests-online/config/config.toml.example` to `tests-online/config/config.toml`.
2. Replace the placeholder token in `tests-online/config/config.toml` with a real api-tst token.
3. Copy `tests-online/config/fixture.env.example` to `tests-online/config/fixture.env`
4. Replace `TEST_BUSINESS_SLUG` in `tests-online/config/fixture.env` with the stable test business slug.
5. Ensure `bash` and `python3` are available.

The real `config.toml` and `fixture.env` are ignored by git.

## Run

```bash
make test-online
```

Each test captures stdout and stderr into a temporary artifact directory, prints a compact
PASS/FAIL line, and leaves failure artifacts on disk for inspection.
