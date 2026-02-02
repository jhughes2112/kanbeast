# KanBeast 🦁

An AI-driven Kanban board system that automatically breaks down feature requests into tasks and executes them using AI agents.

## Overview

KanBeast is a complete kanban management system that combines traditional project management with AI-powered automation. When a ticket is moved to the "Active" column, the system spawns a worker that uses AI agents (Manager and Developer) to automatically implement features, write tests, and manage git workflows.

## Architecture

### Components

1. **KanBeast Server** (C# ASP.NET Core)
   - REST API for ticket and settings management
   - SignalR hub for real-time updates
   - Worker orchestration (spawns Docker containers)
   - Web frontend with drag-and-drop kanban board

2. **KanBeast Worker** (C# Console App)
   - Manager Agent: Breaks down tickets into actionable tasks
   - Developer Agent: Implements features using LLM and tools
   - Git integration: Clones, branches, commits, pushes
   - Tool executor: Bash commands, file operations

### Workflow

1. **User creates ticket** in Backlog column
2. **User drags ticket to Active** → Server spawns Worker container
3. **Manager Agent** analyzes ticket and creates task list
4. **Developer Agent** implements each task
5. **Manager Agent** verifies completion and updates ticket
6. **Worker commits and pushes** changes to git branch
7. **Ticket moves to Testing** → Tests are written and run
8. **Ticket moves to Done** → Branch is rebased to master

## Features

✅ **Kanban Board**
- Drag-and-drop interface
- Four columns: Backlog, Active, Testing, Done
- Real-time updates via SignalR
- Ticket details with task lists and activity logs

✅ **Settings Management**
- Multiple LLM configurations with fallback
- Git repository configuration (URL, SSH key, credentials)
- Custom system prompts for Manager and Developer agents

✅ **Worker Automation**
- Automatic worker spawning when ticket moves to Active
- Git operations (clone, branch, commit, push, rebase)
- Tool execution (bash commands, file editing)
- Task breakdown and verification

✅ **Docker-Based Deployment**
- Server runs in Docker container
- Workers spawn as separate Docker containers
- Docker Compose configuration included

## Getting Started

### Prerequisites

- Docker and Docker Compose
- .NET 9.0 SDK (for local development)
- Git

### Running with Docker Compose

```bash
# Build and start the server
docker-compose up --build

# Access the application
open http://localhost:8080
```

### Local Development

```bash
# Restore dependencies
dotnet restore

# Run the server
cd src/KanBeast.Server
dotnet run

# Run a worker (for testing)
cd src/KanBeast.Worker
dotnet run -- --ticket-id <ticket-guid> --server-url http://localhost:5000
```

## API Endpoints

### Tickets

- `GET /api/tickets` - Get all tickets
- `GET /api/tickets/{id}` - Get specific ticket
- `POST /api/tickets` - Create new ticket
- `PUT /api/tickets/{id}` - Update ticket
- `DELETE /api/tickets/{id}` - Delete ticket
- `PATCH /api/tickets/{id}/status` - Update ticket status
- `POST /api/tickets/{id}/tasks` - Add task to ticket
- `PATCH /api/tickets/{ticketId}/tasks/{taskId}` - Update task status
- `POST /api/tickets/{id}/activity` - Add activity log entry
- `PATCH /api/tickets/{id}/branch` - Set branch name

### Settings

- `GET /api/settings` - Get current settings
- `PUT /api/settings` - Update settings
- `POST /api/settings/llm` - Add LLM configuration
- `DELETE /api/settings/llm/{id}` - Remove LLM configuration

### SignalR Hub

- `/hubs/kanban` - Real-time updates for tickets

## Configuration

### LLM Configuration

Configure multiple LLM providers with priority-based fallback:

```json
{
  "name": "Primary OpenAI",
  "provider": "openai",
  "apiKey": "sk-...",
  "model": "gpt-4",
  "priority": 1,
  "isEnabled": true
}
```

### Git Configuration

```json
{
  "repositoryUrl": "git@github.com:user/repo.git",
  "sshKey": "-----BEGIN OPENSSH PRIVATE KEY-----...",
  "username": "Git User",
  "email": "user@example.com"
}
```

## Project Structure

```
kanbeast/
├── src/
│   ├── KanBeast.Server/
│   │   ├── Controllers/        # API controllers
│   │   ├── Hubs/              # SignalR hubs
│   │   ├── Models/            # Data models
│   │   ├── Services/          # Business logic
│   │   ├── wwwroot/           # Frontend files
│   │   │   ├── css/
│   │   │   ├── js/
│   │   │   └── index.html
│   │   ├── Dockerfile
│   │   └── Program.cs
│   │
│   └── KanBeast.Worker/
│       ├── Agents/            # Manager & Developer agents
│       ├── Models/            # Worker models
│       ├── Services/          # Git, API client, tools
│       ├── Dockerfile
│       └── Program.cs
│
├── docker-compose.yml
├── KanBeast.sln
└── README.md
```

## Technology Stack

- **Backend**: C# 13, .NET 9.0, ASP.NET Core
- **Frontend**: HTML5, CSS3, Vanilla JavaScript
- **Real-time**: SignalR
- **Containerization**: Docker, Docker Compose
- **Version Control**: Git

## Future Enhancements

- [ ] Full LLM integration (OpenAI, Anthropic, Azure)
- [ ] Advanced tool system for agents
- [ ] Test execution and verification
- [ ] Automatic rebase and merge to master
- [ ] User authentication and authorization
- [ ] Persistent storage (database)
- [ ] Webhook integrations
- [ ] Advanced git workflows (pull requests, code reviews)
- [ ] Metrics and analytics dashboard

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

MIT License - See LICENSE file for details

## Author

Built with ❤️ for automated software development
