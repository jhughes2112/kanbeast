# Agent Tool Definitions

This file defines the tools available to agents for structured communication and state transitions.

## Manager Tools

### assign_to_developer

Transfers control to the developer agent with a structured assignment.

**Schema:**
```json
{
  "name": "assign_to_developer",
  "description": "Assign the current subtask to the developer agent",
  "parameters": {
    "type": "object",
    "required": ["mode", "goal", "acceptanceCriteria"],
    "properties": {
      "mode": {
        "type": "string",
        "enum": ["implementation", "testing", "write-tests"],
        "description": "The developer mode to activate"
      },
      "goal": {
        "type": "string",
        "description": "Clear description of what the developer should accomplish"
      },
      "filesToInspect": {
        "type": "array",
        "items": {"type": "string"},
        "description": "File paths the developer should read before making changes"
      },
      "filesToModify": {
        "type": "array",
        "items": {"type": "string"},
        "description": "File paths the developer is expected to create or modify"
      },
      "acceptanceCriteria": {
        "type": "array",
        "items": {"type": "string"},
        "description": "List of criteria that must be met for the subtask to be complete"
      },
      "priorContext": {
        "type": "string",
        "nullable": true,
        "description": "Context from previous attempts or dependent subtasks"
      },
      "constraints": {
        "type": "array",
        "items": {"type": "string"},
        "description": "Rules or patterns the developer must follow"
      }
    }
  }
}
```

**Orchestrator Behavior:**
1. Mark current subtask as "in-progress"
2. Load the developer prompt for the specified mode (`developer-{mode}.txt`)
3. Inject the tool parameters as a "## Current Assignment" section
4. Start developer agent loop
5. Resume manager when developer invokes `subtask_complete`

---

### update_subtask

Updates the status of the current subtask after verification.

**Schema:**
```json
{
  "name": "update_subtask",
  "description": "Update the current subtask status after verification",
  "parameters": {
    "type": "object",
    "required": ["status", "notes"],
    "properties": {
      "status": {
        "type": "string",
        "enum": ["complete", "rejected", "blocked"],
        "description": "The new status for the subtask"
      },
      "notes": {
        "type": "string",
        "description": "Reason for the status change, feedback, or blocker details"
      }
    }
  }
}
```

**Orchestrator Behavior:**
1. Update subtask status in ticket
2. Log the status change to ticket activity
3. If "rejected", increment rejection counter for this subtask
4. If rejection count >= 3, force status to "blocked"
5. Continue manager loop (manager will determine next mode)

---

### complete_ticket

Marks the entire ticket as Done.

**Schema:**
```json
{
  "name": "complete_ticket",
  "description": "Mark the ticket as Done after all work and tests are complete",
  "parameters": {
    "type": "object",
    "required": ["summary"],
    "properties": {
      "summary": {
        "type": "string",
        "description": "Brief summary of all changes made for this ticket"
      }
    }
  }
}
```

**Orchestrator Behavior:**
1. Verify all subtasks are complete
2. Invoke git-oneflow skill to merge changes (if configured)
3. Update ticket status to "Done"
4. Log completion summary to ticket activity
5. End agent loop

---

## Developer Tools

### subtask_complete

Signals the developer has finished work and returns control to the manager.

**Schema:**
```json
{
  "name": "subtask_complete",
  "description": "Signal completion of current work and return control to manager",
  "parameters": {
    "type": "object",
    "required": ["status", "filesChanged", "buildStatus", "message"],
    "properties": {
      "status": {
        "type": "string",
        "enum": ["complete", "blocked"],
        "description": "Whether the work was completed successfully or is blocked"
      },
      "filesChanged": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "path": {"type": "string"},
            "summary": {"type": "string"}
          }
        },
        "description": "List of files that were created or modified"
      },
      "buildStatus": {
        "type": "string",
        "enum": ["pass", "fail"],
        "description": "Whether the solution builds successfully"
      },
      "testResults": {
        "type": "object",
        "nullable": true,
        "properties": {
          "total": {"type": "integer"},
          "passed": {"type": "integer"},
          "failed": {"type": "integer"},
          "skipped": {"type": "integer"}
        },
        "description": "Test results if tests were run (testing/write-tests modes)"
      },
      "message": {
        "type": "string",
        "description": "Summary of what was done"
      },
      "blockerDetails": {
        "type": "object",
        "nullable": true,
        "properties": {
          "issue": {"type": "string"},
          "tried": {"type": "array", "items": {"type": "string"}},
          "needed": {"type": "string"}
        },
        "description": "Details about what is blocking progress (if status is 'blocked')"
      }
    }
  }
}
```

**Orchestrator Behavior:**
1. End developer agent loop
2. Store the completion report for manager context
3. Load manager prompt (`manager-master.txt`)
4. Inject developer's completion report as context
5. Resume manager loop (manager will enter Verify or Blocked mode based on status)

---

## State Tracking

The orchestrator maintains the following state:

| State | Type | Description |
|-------|------|-------------|
| currentAgent | enum | "manager" or "developer" |
| currentSubtaskId | string | ID of the subtask currently being worked |
| currentDeveloperMode | string | "implementation", "testing", or "write-tests" |
| rejectionCounts | map | subtaskId → rejection count |
| lastDeveloperResult | object | The last `subtask_complete` parameters |
| lastManagerAssignment | object | The last `assign_to_developer` parameters |

The orchestrator is responsible for:
- Loading the correct prompt file based on agent and mode
- Injecting context (assignment, completion report, ticket state)
- Tracking subtask IDs so agents don't need to
- Enforcing iteration limits
