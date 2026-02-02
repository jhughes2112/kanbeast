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

The server will start on http://localhost:5042

2. **Open the Web Interface**

Navigate to http://localhost:5042 in your browser

3. **Create a Ticket**

- Click "+ New Ticket" button
- Fill in the title and description
- Click "Create Ticket"

4. **Move Ticket to Active**

- Drag and drop the ticket from Backlog to Active column
- This will automatically spawn a worker (simulated in this version)
- Watch the activity log for updates

5. **Test the Worker (Optional)**

To manually test the worker:

```bash
cd src/KanBeast.Worker

# Get a ticket ID from the API
curl http://localhost:5042/api/tickets

# Run the worker with that ticket ID
dotnet run -- --ticket-id <YOUR-TICKET-ID> --server-url http://localhost:5042
```

## Running with Docker

### Build Images

```bash
# Build server image
docker build -t kanbeast-server -f src/KanBeast.Server/Dockerfile .

# Build worker image
docker build -t kanbeast-worker -f src/KanBeast.Worker/Dockerfile .
```

### Run with Docker Compose

```bash
docker-compose up --build
```

Access the application at http://localhost:8080

## API Examples

### Create a Ticket
```bash
curl -X POST http://localhost:5042/api/tickets \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Implement feature X",
    "description": "Add cool new feature",
    "status": 0,
    "tasks": [],
    "activityLog": []
  }'
```

### Get All Tickets
```bash
curl http://localhost:5042/api/tickets | jq .
```

### Update Ticket Status (Move to Active)
```bash
curl -X PATCH "http://localhost:5042/api/tickets/{TICKET_ID}/status" \
  -H "Content-Type: application/json" \
  -d '{"status": 1}'
```

Status values:
- 0 = Backlog
- 1 = Active
- 2 = Testing
- 3 = Done

### Add Activity to Ticket
```bash
curl -X POST "http://localhost:5042/api/tickets/{TICKET_ID}/activity" \
  -H "Content-Type: application/json" \
  -d '{"message": "Started implementation"}'
```

## Configuration

### Settings Page

Access http://localhost:5042 and click "⚙️ Settings" to configure:

1. **Git Configuration**
   - Repository URL
   - Username and Email
   - SSH Key (for private repos)

2. **LLM Configuration** (Future)
   - Add multiple LLM providers
   - Set priorities for fallback
   - Configure API keys and models

## Architecture Notes

The system consists of two main components:

1. **KanBeast.Server**: ASP.NET Core Web API + SignalR + Frontend
   - Manages tickets, tasks, and settings
   - Spawns worker containers when tickets move to Active
   - Provides real-time updates via SignalR

2. **KanBeast.Worker**: Console application
   - Manager Agent: Breaks down tickets into tasks
   - Developer Agent: Implements features
   - Git operations: clone, branch, commit, push
   - Tool execution: bash commands, file editing

## Future Features

The current implementation provides the core architecture and infrastructure. Future enhancements will include:

- Full LLM integration (OpenAI, Anthropic, Azure)
- Actual Docker container spawning for workers
- Advanced tool system with more capabilities
- Test execution and verification
- Automatic git rebase and merge
- User authentication
- Persistent database storage
- Webhooks and integrations

## Troubleshooting

### Port Already in Use

If port 5042 is in use:

```bash
# Edit src/KanBeast.Server/Properties/launchSettings.json
# Change the port numbers to something else
```

### Docker Build Fails

Make sure you're running from the repository root:

```bash
cd /path/to/kanbeast
docker build -t kanbeast-server -f src/KanBeast.Server/Dockerfile .
```

### Worker Can't Connect

Make sure the server URL is correct and reachable from the worker:

```bash
# Test server connectivity
curl http://localhost:5042/api/tickets
```
