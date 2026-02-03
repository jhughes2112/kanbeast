# Quick Start Guide

## Running Locally

### Prerequisites
- .NET 9.0 SDK
- Git

### Steps

1. **Start the Server**
```bash
cd src/KanBeast.Server
dotnet run
```

The server will start on http://localhost:5000

2. **Open the Web Interface**

Navigate to http://localhost:5000 in your browser

3. **Create a Ticket**

- Click "+ New Ticket" button
- Fill in the title and description
- Click "Create Ticket"

4. **Move Ticket to Active**

- Drag and drop the ticket from Backlog to Active column
- This will trigger a worker to process the ticket
- Watch the activity log for updates

5. **Test the Worker (Optional)**

To manually test the worker:

```bash
cd src/KanBeast.Worker

# Get a ticket ID from the API
curl http://localhost:5000/api/tickets

# Run the worker with that ticket ID
dotnet run -- --ticket-id <YOUR-TICKET-ID> --server-url http://localhost:5000
```

## Configuration

### Settings Page

Access http://localhost:5000 and click "Settings" to configure:

1. **Git Configuration**
   - Repository URL
   - Username and Email
   - SSH Key (for private repos)

2. **LLM Configuration**
   - Add multiple LLM providers
   - Configure API keys and models

### Worker Configuration Files

The worker reads configuration from `env/` directory:

- `env/settings.json` - LLM configs, Git config, retry settings
- `env/prompts/*.txt` - Prompt templates for manager and developer agents

## Architecture Notes

The system consists of two main components:

1. **KanBeast.Server**: ASP.NET Core Web API + SignalR + Frontend
   - Manages tickets, tasks, and settings
   - Spawns worker processes when tickets move to Active
   - Provides real-time updates via SignalR

2. **KanBeast.Worker**: Console application
   - AgentOrchestrator: Controls Manager/Developer loop
   - Manager Agent: Breaks down tickets, assigns work, verifies
   - Developer Agent: Implements features using tools
   - Git operations: clone, branch, commit, push

## Troubleshooting

### Port Already in Use

If port 5000 is in use, specify a different port:

```bash
dotnet run --urls http://localhost:5001
```

### Worker Cannot Connect

Make sure the server URL is correct and reachable:

```bash
# Test server connectivity
curl http://localhost:5000/api/tickets
```

### Missing Prompt Files

The worker requires prompt files in `env/prompts/`. If missing, the worker will fail to start. Check for these required files:

- manager-master.txt
- manager-breakdown.txt
- manager-assign.txt
- manager-verify.txt
- developer-implementation.txt
- developer-testing.txt
