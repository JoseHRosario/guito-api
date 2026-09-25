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