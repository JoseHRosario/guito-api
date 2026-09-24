# Guito Code Conventions

Rules that apply to every PR in this repo. AGENTS.md carries architecture and workflow;
this file carries coding conventions. Agents must check this list before committing.

## One class or interface per file

A source file declares exactly **one** type. Records count as types. Supporting records
that belong to a single class conceptually (e.g. a result record) go in their own file
next to it — same namespace, file named after the type.

- Bad: `JwksClient.cs` with `IJwksClient` + `HttpJwksClient`
- Good: `IJwksClient.cs`, `HttpJwksClient.cs`, `JsonWebKey.cs`

Nested private helper classes inside a test fixture class are fine; the rule is about
top-level declared types.