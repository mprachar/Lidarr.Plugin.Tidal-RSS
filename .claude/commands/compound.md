---
allowed-tools: Task, Read, Write, Edit, Glob, Grep, Bash
description: Capture learnings from completed task to compound future work
argument-hint: [pr_url, issue_number, or task_description]
---

# Compound - Capture Learnings

Systematically capture learnings from completed work to make future tasks easier. Run this after merging a PR or completing significant work.

**IMPORTANT: This is a personal project. All learnings are stored in auto-memory ONLY (not in the repo). No docs/ directory is created.**

## Instructions

- Accept PR URL, issue number, or free-form task description
- Use parallel agents to analyze the completed work
- Synthesize learnings into structured knowledge
- Store in auto-memory ONLY (outside the repo)
- Update Deep Dive Learnings pointer in CLAUDE.md
- Report what was captured for future reference

## Input

$ARGUMENTS

## Phase 0: Input Parsing

**Step 0.1: Parse Input**

Determine input type:
- If input looks like a GitHub PR URL (contains `/pull/`): Fetch PR details
- If input looks like an issue number (e.g., `#123` or just `123`): Fetch issue details
- Otherwise: Treat as free-form task description

**Step 0.2: Gather Context**

If PR URL provided:
* INVOKE Task tool:
  - subagent_type: "sentinel"
  - description: "Fetch PR details"
  - prompt: "Fetch PR details for: {input}. Run: `gh pr view {url} --json title,body,files,additions,deletions,commits,mergedAt,author`. Also run `gh pr diff {url}` to get the full diff. Return: (1) PR title and description, (2) Files changed with additions/deletions, (3) Key commits, (4) Full diff summary."

If issue number provided:
* INVOKE Task tool:
  - subagent_type: "sentinel"
  - description: "Fetch issue details"
  - prompt: "Fetch issue details for: #{input}. Run: `gh issue view {number} --json title,body,labels,state`. Return issue context."

**Step 0.3: Identify Task Type**

Classify the completed work:
- **bug**: Fixed broken behavior, error, or regression
- **feature**: Added new functionality or enhancement
- **chore**: Maintenance, refactoring, dependency update
- **refactor**: Restructured code without changing behavior

## Phase 1: Context Gathering (Parallel Agents)

Launch 3 agents in parallel to analyze the completed work:

**Step 1.1: Implementation Analysis** (parallel)
* INVOKE Task tool:
  - subagent_type: "codebase-researcher"
  - description: "Analyze implementation patterns"
  - prompt: "Analyze the completed work: {task_description}. Files changed: {files_list}. Identify: (1) Patterns used in the implementation, (2) Architecture decisions visible in the code, (3) Integration points created or modified, (4) Testing approach used, (5) Code conventions followed. Provide structured analysis with file:line references."

**Step 1.2: Decision Extraction** (parallel)
* INVOKE Task tool:
  - subagent_type: "linus-kernel-planner"
  - description: "Extract key decisions"
  - prompt: "Review the completed work: {task_description}. Diff: {diff_summary}. Extract: (1) Key decisions made and WHY they were made, (2) Alternative approaches that were likely considered and rejected, (3) Simplifications achieved (complexity avoided), (4) Trade-offs accepted, (5) What makes this solution good (or problematic). Be specific about the reasoning behind choices."

**Step 1.3: Problem-Solution Mapping** (parallel)
* INVOKE Task tool:
  - subagent_type: "debug-detective"
  - description: "Document problem-solution"
  - prompt: "Analyze the problem-solution for: {task_description}. Document: (1) Original problem statement (what was broken or missing), (2) Root cause (if bug fix), (3) Solution approach taken, (4) Edge cases handled, (5) Gotchas discovered during implementation, (6) What someone working in this area should know. Focus on practical insights."

## Phase 2: Knowledge Synthesis

**Step 2.1: Compile Learnings**

Wait for all three agents, then synthesize:

1. **Decisions**: Key choices and their rationale
2. **Patterns**: Reusable approaches or code patterns
3. **Mistakes**: What went wrong or almost went wrong
4. **Gotchas**: Non-obvious things to watch out for

**Step 2.2: Determine Significance**

Assess the significance of this work:
- **Major**: New architecture pattern, significant feature, complex bug fix, reusable solution
- **Minor**: Small fix, routine change, project-specific detail

## Phase 3: Knowledge Storage (Auto-Memory ONLY)

**Step 3.1: Write detailed topic file**

Write to auto-memory: `~/.claude/projects/.../memory/{topic-slug}.md`

Include ALL findings from Phase 1 agents in structured format:
```markdown
# {Topic Title}

**Date**: {today's date}
**PR**: {pr_url if available}
**Type**: {bug|feature|chore|refactor}
**Significance**: {Major|Minor}

## Problem
{From Phase 1.3}

## Root Cause
{If applicable}

## Solution
{From Phase 1.2 - approach with key decisions}

## Key Files
{From Phase 1.1 - file:line references}

## Patterns Used
{From Phase 1.1}

## Gotchas
{From Phase 1.3}

## Decisions & Trade-offs
{From Phase 1.2}
```

**Step 3.2: Update MEMORY.md learnings index**

Add a row to the `## Learnings Index` table in `~/.claude/projects/.../memory/MEMORY.md`:
```
| {topic} | `{topic-slug}.md` | {one-line summary} |
```

**Step 3.3: Update CLAUDE.md Deep Dive pointer**

Add a row to the `## Deep Dive Learnings` table in the project's `CLAUDE.md`:
```
| {topic} | `{topic-slug}.md` | {today's date} |
```

## Phase 4: Report

Provide summary to user:

```
## Compound Complete

**Task**: {task_description}
**Type**: {bug|feature|chore|refactor}
**Significance**: {Major|Minor}

### Learnings Captured

**Decisions**:
- {decision 1}
- {decision 2}

**Patterns**:
- {pattern 1}

**Gotchas**:
- {gotcha 1}

### Files Updated (Auto-Memory Only)

- {memory/topic-slug.md}: Detailed investigation notes
- MEMORY.md: Updated learnings index
- CLAUDE.md: Updated Deep Dive pointer

### Knowledge Now Available

This learning will be surfaced in future sessions via CLAUDE.md pointer and auto-memory index.
```

## Output Format

* **TaskSummary**: What was completed (brief)
* **AgentFindings**: Key insights from each of the 3 agents
* **LearningSynthesis**: Decisions, patterns, mistakes, gotchas extracted
* **StorageActions**: Auto-memory files created or updated
* **FutureValue**: How this will help future work
