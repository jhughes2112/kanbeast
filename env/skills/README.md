# Skills

Each subfolder here is a different skill that can be utilized to perform some action. You may create new skills if none here suit your needs.

## Skill Folder Structure

```
<Skill-Name>/
??? Skill.md
??? resources/ (optional)
??? scripts/ (optional)
```

The folder name should match the Skill name. The root file must be `Skill.md`.

## Skill.md Format

`Skill.md` must start with YAML frontmatter containing required metadata:

```yaml
---
name: Build Project
description: Build .NET solutions and projects with structured error parsing.
dependencies: dotnet
---
```

### Required Metadata Fields

| Field | Description |
|-------|-------------|
| `name` | Human-friendly Skill name (max 64 characters) |
| `description` | What the Skill does and when to use it (max 200 characters) |

### Optional Metadata Fields

| Field | Description |
|-------|-------------|
| `dependencies` | Packages/tools required by the Skill |

## Markdown Body

After the frontmatter, add the Skill's instructions, examples, and references. Keep it focused on one workflow. Include example inputs/outputs when helpful.

## Adding Resources

If the Skill needs additional reference material, add files like `REFERENCE.md` alongside `Skill.md` and mention them in the body so they can be loaded when needed.

## Adding Scripts

You can include executable scripts (e.g., Python or Bash) in the Skill folder and reference them from `Skill.md`. Ensure any required dependencies are listed in the frontmatter.

## Available Skills

| Skill | Description |
|-------|-------------|
| [build-project](./build-project/Skill.md) | Build .NET solutions with structured error parsing |
| [run-tests](./run-tests/Skill.md) | Execute .NET test suites with structured output |
| [git-oneflow](./git-oneflow/Skill.md) | Git workflow with feature branches and fast-forward merges |