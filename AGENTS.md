# Project Working Instructions

## Before making changes

Do not edit, create, or delete any file until I have approved a plan.

For any request that would change files:
1. State what you understand the request to be.
2. Show exactly what you propose to change — files, and what
   changes in each.
3. Say why each change is needed.
4. List the steps in order.
5. Stop and wait. I will discuss, amend, and give instructions.

Only make the changes I approve. If I approve part of a plan,
do only that part.

## When you are unsure

Ask. Do not guess and do not proceed on an assumption.
Specifically, ask when:
- The request is ambiguous or could mean more than one thing.
- You need detail I have not given (paths, versions, expected
  behaviour, edge cases).
- You are uncertain whether an approach is what I want.

One clear question is better than a wrong change.

## Frontend development ports

- Reserve port **3000 for the user**.
- Use port **3001 for frontend servers started by Codex**.
- Never stop or restart the user's server on port 3000.
- If port 3001 is occupied, ask before stopping its process or choosing another port.
- Prefer a command-line port override rather than changing the project's default port.

# MyShoppingList — General Feature Design and Quality Rules

These rules apply to every new feature, enhancement and modification in MyShoppingList.

## Core principle

**Do not merely implement a requested feature. Implement it as a complete, intuitive, reliable and well-thought-out part of the product.**

The goal is not simply to make the code work. The goal is to make the feature genuinely useful, easy to understand, consistent with the rest of the application and pleasant to use.

## 1. Understand the real purpose

Before implementing a feature:

- Understand what problem the user is trying to solve.
- Consider how a real customer would naturally expect it to behave.
- Consider the full user journey, not just the individual button or function.
- Identify likely edge cases, misunderstandings and unintended consequences.
- Avoid implementing requirements literally when doing so would produce an awkward experience.

The feature should solve the user's problem, not merely satisfy a technical specification.

## 2. User experience comes first

Every feature should be:

- **Intuitive:** Users can understand what to do without reading instructions.
- **Simple:** Avoid unnecessary steps, controls and complexity.
- **Consistent:** Follow established interface and interaction patterns.
- **Predictable:** Actions should produce expected results.
- **Helpful:** Provide sensible defaults and useful feedback.
- **Forgiving:** Allow users to correct mistakes, change their minds and undo actions where practical.
- **Accessible:** Work with keyboards, screen readers and mobile devices where relevant.

Do not expose technical complexity to users unnecessarily.

## 3. Think through the entire workflow

Consider every relevant state:

- Before the user starts
- While the operation is processing
- After successful completion
- When no results are found
- When partial results are available
- When an operation fails
- When the user cancels or changes their mind
- When data is missing, incorrect or outdated

Users should understand what happened and what they can do next.

## 4. Make intelligent design decisions

Do not add functionality simply because it is technically possible.

Consider whether it genuinely improves the product.

Use sensible defaults and avoid unnecessary configuration.

If several possible implementations exist, prefer the one that provides the clearest user experience with the least unnecessary complexity.

Keep advanced capabilities available without overwhelming ordinary users.

## 5. Correctness and trust

The application must do what it claims to do.

- Never show misleading information.
- Do not present uncertain data as confirmed.
- Clearly distinguish exact matches from alternatives.
- Display relevant limitations when they affect decisions.
- Handle unexpected inputs and failure conditions.
- Preserve data integrity, security and privacy.
- Avoid silent failures.

For shopping recommendations, a technically valid calculation is not enough. The recommendation must also make practical sense to the customer.

## 6. Implementation quality

Write maintainable, secure, testable code.

- Reuse existing architecture and conventions.
- Keep business logic separate from presentation logic.
- Avoid unnecessary dependencies and overengineering.
- Preserve backward compatibility where practical.
- Do not introduce regressions into existing features.
- Test normal usage, edge cases and failure scenarios.

## 7. Validate from the customer's perspective

Before declaring a feature complete, ask:

1. Is this genuinely useful?
2. Would a first-time user understand it?
3. Does it behave as a user would naturally expect?
4. Are its defaults sensible?
5. Have important edge cases been handled?
6. Does it give clear feedback?
7. Does it work correctly on mobile and desktop?
8. Is the information accurate and trustworthy?
9. Does it integrate naturally with the existing application?
10. Would this feel like a polished feature in a professional commercial product?

Where practical, validate the complete user journey using automated tests and browser-based interaction tests, not only unit tests.

Do not claim the user experience has been verified unless it has actually been tested.

## 8. Definition of done

A feature is not complete simply because its code compiles or its API returns the expected result.

A feature is complete only when:

- Its intended user problem is solved.
- Its primary workflow works end to end.
- Its interface is intuitive and consistent.
- Relevant edge cases and failure states are handled.
- Security, privacy and data integrity are preserved.
- Appropriate tests pass.
- Its implementation does not introduce known regressions.
- Any remaining limitations are clearly documented.

If a requirement produces a poor user experience or conflicts with established product behaviour, explain the concern and propose a better approach rather than blindly implementing it.

**Build features as a thoughtful product designer and a responsible software engineer, not merely as a code generator.**