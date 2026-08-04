# Online Regression Tests

This directory contains the shell-based online regression suite for the `nocfo` CLI.

The suite is intentionally separate from the offline `dotnet test` path. It runs the
existing CLI end to end against `https://api-tst.nocfo.io` using a repo-local profile
selected through `NOCFO_TOOL_CONFIG_HOME`.

## Setup

1. Copy `tests-online/config/config.toml.example` to `tests-online/config/config.toml`.
2. Issue an api-tst token at <https://login-tst.nocfo.io/auth/tokens/> and put it in
   `tests-online/config/config.toml` in place of the placeholder.
3. Copy `tests-online/config/fixture.env.example` to `tests-online/config/fixture.env`
4. Replace `TEST_BUSINESS_SLUG` in `tests-online/config/fixture.env` with the stable test business slug.
5. Ensure `bash` and `python3` are available.

The real `config.toml` and `fixture.env` are ignored by git.

Tokens expire and can be revoked, so a suite that has not been run for a while will
typically stop at the authentication preflight. Repeat step 2 to recover; the API
answers a dead token with `401 Epäkelpo token.` and the CLI exits 77.

The suite unsets `NOCFO_TOKEN`, `NOCFO_TARGET_TOKEN`, `NOCFO_SOURCE_TOKEN` and the
matching `*_BASE_URL` variables before running, because those would otherwise override
the `online-test` profile and could aim the mutation tests at production.

## Run

```bash
make test-online
```

Each test captures stdout and stderr into a temporary artifact directory, prints a compact
PASS/FAIL line, and leaves failure artifacts on disk for inspection.
