# Guito Code Conventions

Rules that apply to every PR in this repo. AGENTS.md carries architecture and workflow;
this file carries coding conventions. Agents must check this list before committing.

## Solution file (guito-api.slnx) membership

Keep the solution file a complete map of the repo:
1. **When a DB migration is created** (`db/migrations/NNN_*.sql`), add it to the solution file inside a `/db/migrations/` folder block (like the `/docs/` folder below).
2. **When an ADR is created** (`docs/adr/NNNN-*.md`), add it to the solution file's `/docs/` folder.
3. The solution is the first place a reader sees the repo shape — a file missing from it is a review finding.

## Branching

One branch per issue, cut from `master`, named with the issue number first:

- Features and new work: `feature/<issue#>-<slug>` (e.g. `feature/9-user-flow`)
- Bugs — something that worked as intended is broken (code regression, wrong
  behavior, broken config): `bug/<issue#>-<slug>` (e.g. `bug/40-token-crash`)
- Environment/CI/docs work stays under the existing informal prefixes (`chore/`,
  `docs/`) — not formalized further.

The PR body must end with `Closes #N`. Never push directly to `master`.

## One class or interface per file

A source file declares exactly **one** type. Records count as types. Supporting records
that belong to a single class conceptually (e.g. a result record) go in their own file
next to it — same namespace, file named after the type.

- Bad: `JwksClient.cs` with `IJwksClient` + `HttpJwksClient`
- Good: `IJwksClient.cs`, `HttpJwksClient.cs`, `JsonWebKey.cs`

Nested private helper classes inside a test fixture class are fine; the rule is about
top-level declared types.

## Type homes (where a class lives, by what it is)

The purpose of a type determines its folder — the same shape in two places is NOT
duplicated code if the purposes differ (Clean Architecture: each boundary owns its own
model; nothing crosses a boundary in a foreign layer's type):

- `DataTransferObjects/` — HTTP wire contracts ONLY: request/response bodies of the API
  (`Input/` for request DTOs, `Output/` for response DTOs). If a type never touches a
  controller, it does not belong here.
- `Model/` — internal domain/application payloads: the vocabulary services and
  repositories exchange (`UserIdentity`, `BankTransactionInsert`,
  `BankTransactionPendingDetail`). Never a request body, never SQL or wire-shaped.
- `Repositories/` — repository INTERFACES the services define (ADR-0011).
- `Infrastructure/` — technology adapters implementing repository interfaces; SQL,
  ranges, HTTP transports, mappers live only here.

Boundaries translate mechanically between homes: controllers map wire DTO ↔ Model,
services map external payloads (e.g. Enable Banking JSON) ↔ Model. A repository
interface must not take a `DataTransferObjects/` type (known debt: `IExpenseRepository`
currently takes `ExpenseCreate` — refactor tracked separately).

---

# C# Clean Code & Coding Conventions

Follow these architectural, design, and formatting conventions strictly when writing,
reviewing, or refactoring C# code in this repo.

## 1. Naming & Style Conventions (.NET Standard)

### Always use:
- **PascalCase:** class names, structures, records, enums, interfaces, methods, properties, and namespaces.
- **camelCase:** method arguments, local variables, and inline parameters.
- **Interface prefixing:** interface names start with a capital `I` (e.g. `IUserRepository`).
- **Meaningful names:** names must reveal intent. Avoid abbreviations (use `userRepository`, never `uRep`).

### Private field rules:
- Prefix private fields with an underscore `_` and use camelCase (e.g. `_databaseContext`).
- Do not use `this.` when accessing private fields prefixed with `_`.

## 2. Modern C# Features & Conciseness

Leverage modern C# syntax (C# 10+) to reduce boilerplate and maximize readability.

- **Expression-bodied members:** use `=>` for single-line methods, properties, and local functions.
- **Pattern matching:** prefer modern `switch` expressions and recursive patterns over complex `if-else` or legacy `switch-case` statements.
- **Block-scoped usings:** use `using var` instead of nested `using (...) { }` blocks to avoid unnecessary nesting levels.
- **String interpolation:** always use `$"..."` instead of string concatenation or `string.Format()`.

## 3. Structural & Architectural Principles

### Guard clauses (early return)
- Never nest logic deep inside multiple `if` blocks.
- Validate parameters and edge cases at the very beginning of the method. Return early or throw immediately. Nesting must not exceed 2 levels deep.

```csharp
// ❌ BAD: deep nesting, manual null checks
public void ProcessOrder(Order order)
{
    if (order != null)
    {
        if (order.IsPaid)
        {
            // Business logic here
        }
    }
}

// ✅ GOOD: guard clauses, modern pattern matching
public void ProcessOrder(Order order)
{
    if (order is not { IsPaid: true }) return;

    // Business logic here
}
```

### Single Responsibility Principle (SRP)
- Functions do **one thing**, completely and well.
- Limit methods to fewer than 20 lines. If a method handles multiple steps, extract them into private helper methods.

### Async/await best practices
- Always append the `Async` suffix to methods returning a `Task` or `ValueTask`.
- Propagate `CancellationToken` through all asynchronous calls where available.

## 4. Comments & Documentation Standards

- Write self-explanatory code; do not comment *what* the code does when it is readable.
- **Comment the "why":** comments explain complex business rules, non-obvious technical decisions, or workarounds only.
- **No dead code:** never leave commented-out code blocks. Delete them immediately.
- XML `///` documentation only for public APIs, interfaces, shared library entry points, or highly complex domain methods; keep summaries concise.

```csharp
// ❌ BAD: commenting the obvious
// Check if user is adult
if (user.Age >= 18) { /* ... */ }

// ✅ GOOD: documenting a non-obvious business rule
// Threshold is set to 21 due to regional compliance regulation updates (Policy #402)
if (user.Age >= 21) { /* ... */ }
```

## 5. Unit Testing Standards

xUnit (the repo's test framework), following these practices:

### Naming
`MethodUnderTest_ShouldExpectedBehavior_WhenStateUnderTest` — the test report reads like a specification.

### Structure
Explicit **Arrange, Act, Assert (AAA)** blocks separated by blank lines. One focused behavior per test method.

```csharp
// ❌ BAD: ambiguous name, mixed steps, unclear goal
[Fact]
public void TestDiscount()
{
    var cart = new ShoppingCart();
    cart.Add(new Item(100));
    cart.ApplyCoupon("SAVE10");
    Assert.Equal(90, cart.Total);
}

// ✅ GOOD: explicit naming, AAA structure, clear intent
[Fact]
public void ApplyCoupon_ShouldDeductTenPercent_WhenCouponIsValid()
{
    // Arrange
    var cart = new ShoppingCart();
    cart.Add(new Item(100));
    var validCoupon = "SAVE10";

    // Act
    cart.ApplyCoupon(validCoupon);

    // Assert
    Assert.Equal(90, cart.Total);
}
```

## 6. Verification checklist

Before committing any C# change, verify that:
1. No variables or magic strings are cryptically named.
2. Nesting does not exceed 2 levels deep.
3. All `IDisposable` resources use `using var` syntax.
4. Methods that can be expression-bodied are converted.
5. Unit tests follow AAA structure and the naming pattern.