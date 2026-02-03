# KanBeast

An AI-driven Kanban board system that automatically breaks down feature requests into tasks and executes them using AI agents.

## Overview

KanBeast is a complete kanban management system that combines traditional project management with AI-powered automation. When a ticket is moved to the Active column, the system spawns a worker that uses a Manager/Developer agent orchestration loop to automatically implement features, write tests, and manage git workflows.

## Architecture

### Components

**KanBeast Server** (C# ASP.NET Core)
- REST API for ticket and settings management
- SignalR hub for real-time updates
- Static file hosting for web frontend
- Single-port deployment (API, SignalR, and frontend on same origin)

**KanBeast Worker** (C# Console App)
- AgentOrchestrator: Manages Manager/Developer agent loop
- Manager Agent: Breaks down tickets, assigns work, verifies completion
- Developer Agent: Implements features using LLM and tools
- Git integration: Clones, branches, commits, pushes

### Agent Workflow

The orchestrator maintains state and switches between agents based on tool invocations:

1. Manager calls `assign_to_developer` -> switches to Developer
2. Developer calls `subtask_complete` -> switches back to Manager
3. Manager calls `update_subtask` to mark work complete or rejected
4. Manager calls `complete_ticket` when all work is done

### Ticket Lifecycle

1. User creates ticket in Backlog column
2. User moves ticket to Active -> Server spawns Worker
3. Manager Agent analyzes ticket and creates subtask list
4. Manager assigns subtask to Developer via `assign_to_developer`
5. Developer Agent implements the subtask using tools
6. Developer signals completion via `subtask_complete`
7. Manager verifies work and marks subtask complete or rejected
8. Repeat 4-7 for each subtask
9. Manager marks ticket Done via `complete_ticket`

## Features

### Kanban Board
- Drag-and-drop interface
- Four columns: Backlog, Active, Testing, Done
- Real-time updates via SignalR
- Restricted moves: Backlog to Active (start), anywhere to Backlog (cancel)

### Agent Tools

**Manager Tools:**
- `assign_to_developer`: Transfer control to developer with structured assignment
- `update_subtask`: Mark subtask complete, rejected, or blocked
- `complete_ticket`: Finalize ticket after all work is done

**Developer Tools:**
- `subtask_complete`: Signal work done and return control to manager

### Orchestration Features
- Automatic rejection escalation (3 rejections -> blocked status)
- Developer mode switching (implementation, testing, write-tests)
- Context injection from manager assignment to developer
- Completion report passed from developer back to manager

## Getting Started

### Prerequisites

- .NET 9.0 SDK
- Git

### Running Locally

Start the server:

```bash
cd src/KanBeast.Server
dotnet run
```

Open http://localhost:5000 in your browser.

### Running the Worker

The worker requires configuration files in the env directory.

Required files:
- `env/settings.json` - LLM and Git configuration
- `env/prompts/*.txt` - Prompt templates

Run worker:

```bash
cd src/KanBeast.Worker
dotnet run -- --ticket-id <TICKET_ID> --server-url http://localhost:5000
```

## API Endpoints

### Tickets

- `GET /api/tickets` - Get all tickets
- `GET /api/tickets/{id}` - Get specific ticket
- `POST /api/tickets` - Create new ticket
- `PATCH /api/tickets/{id}/status` - Update ticket status
- `POST /api/tickets/{id}/tasks` - Add task to ticket
- `POST /api/tickets/{id}/activity` - Add activity log entry

### Settings

- `GET /api/settings` - Get current settings
- `PUT /api/settings` - Update settings

### SignalR Hub

Connect to `/hubs/kanban` for real-time updates.

Events:
- `TicketUpdated` - Ticket was modified
- `TicketCreated` - New ticket created
- `TicketDeleted` - Ticket was deleted

## Technology Stack

- Backend: C# 13, .NET 9, ASP.NET Core
- Frontend: HTML5, CSS3, Vanilla JavaScript
- Real-time: SignalR
- LLM: Microsoft Semantic Kernel
- Version Control: Git
