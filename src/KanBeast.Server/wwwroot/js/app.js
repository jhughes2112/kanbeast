// API Base URL
const API_BASE = '/api';

// SignalR connection
let connection = null;

// State
let tickets = [];
let settings = null;

// Initialize the application
async function init() {
    await loadTickets();
    await loadSettings();
    setupSignalR();
    setupEventListeners();
    setupDragAndDrop();
}

// Setup SignalR for real-time updates
function setupSignalR() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/kanban")
        .withAutomaticReconnect()
        .build();

    connection.on("TicketUpdated", (ticket) => {
        updateTicketInUI(ticket);
    });

    connection.on("TicketCreated", (ticket) => {
        tickets.push(ticket);
        renderTicket(ticket);
    });

    connection.on("TicketDeleted", (ticketId) => {
        tickets = tickets.filter(t => t.id !== ticketId);
        document.querySelector(`[data-ticket-id="${ticketId}"]`)?.remove();
    });

    connection.start().catch(err => console.error('SignalR connection error:', err));
}

// Load all tickets
async function loadTickets() {
    try {
        const response = await fetch(`${API_BASE}/tickets`);
        tickets = await response.json();
        renderAllTickets();
    } catch (error) {
        console.error('Error loading tickets:', error);
    }
}

// Load settings
async function loadSettings() {
    try {
        const response = await fetch(`${API_BASE}/settings`);
        settings = await response.json();
    } catch (error) {
        console.error('Error loading settings:', error);
    }
}

// Render all tickets
function renderAllTickets() {
    // Clear all containers
    document.querySelectorAll('.ticket-container').forEach(container => {
        container.innerHTML = '';
    });

    // Render each ticket in the appropriate column
    tickets.forEach(ticket => renderTicket(ticket));
}

// Render a single ticket
function renderTicket(ticket) {
    const container = document.getElementById(`${ticket.status.toLowerCase()}-container`);
    if (!container) return;

    // Remove existing ticket if it exists
    const existingTicket = document.querySelector(`[data-ticket-id="${ticket.id}"]`);
    if (existingTicket) {
        existingTicket.remove();
    }

    const completedTasks = ticket.tasks.filter(t => t.isCompleted).length;
    const totalTasks = ticket.tasks.length;

    const ticketElement = document.createElement('div');
    ticketElement.className = 'ticket';
    ticketElement.draggable = true;
    ticketElement.dataset.ticketId = ticket.id;
    ticketElement.innerHTML = `
        <div class="ticket-title">${escapeHtml(ticket.title)}</div>
        <div class="ticket-description">${escapeHtml(ticket.description)}</div>
        ${totalTasks > 0 ? `
            <div class="ticket-tasks">
                <span class="ticket-tasks-summary">Tasks: ${completedTasks}/${totalTasks}</span>
            </div>
        ` : ''}
        <div class="ticket-meta">
            <span>${new Date(ticket.createdAt).toLocaleDateString()}</span>
            ${ticket.workerId ? `<span>👷 Worker Active</span>` : ''}
        </div>
    `;

    ticketElement.addEventListener('click', () => showTicketDetails(ticket.id));
    container.appendChild(ticketElement);
}

// Update ticket in UI
function updateTicketInUI(ticket) {
    const index = tickets.findIndex(t => t.id === ticket.id);
    if (index !== -1) {
        tickets[index] = ticket;
    }
    renderTicket(ticket);
}

// Show ticket details
async function showTicketDetails(ticketId) {
    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}`);
        const ticket = await response.json();

        const modal = document.getElementById('ticketDetailModal');
        const detailDiv = document.getElementById('ticketDetail');

        detailDiv.innerHTML = `
            <h2>${escapeHtml(ticket.title)}</h2>
            <p><strong>Status:</strong> ${ticket.status}</p>
            <p><strong>Description:</strong> ${escapeHtml(ticket.description)}</p>
            ${ticket.branchName ? `<p><strong>Branch:</strong> ${escapeHtml(ticket.branchName)}</p>` : ''}
            
            <h3>Tasks</h3>
            ${ticket.tasks.length > 0 ? `
                <ul class="task-list">
                    ${ticket.tasks.map(task => `
                        <li class="task-item ${task.isCompleted ? 'completed' : ''}">
                            <input type="checkbox" ${task.isCompleted ? 'checked' : ''} disabled>
                            ${escapeHtml(task.description)}
                        </li>
                    `).join('')}
                </ul>
            ` : '<p>No tasks yet</p>'}
            
            <h3>Activity Log</h3>
            ${ticket.activityLog.length > 0 ? `
                <div class="activity-log">
                    ${ticket.activityLog.map(log => `
                        <div class="activity-item">${escapeHtml(log)}</div>
                    `).join('')}
                </div>
            ` : '<p>No activity yet</p>'}
        `;

        modal.classList.add('active');

        // Subscribe to updates for this ticket
        if (connection) {
            connection.invoke("SubscribeToTicket", ticketId);
        }
    } catch (error) {
        console.error('Error loading ticket details:', error);
    }
}

// Setup drag and drop
function setupDragAndDrop() {
    document.querySelectorAll('.ticket-container').forEach(container => {
        container.addEventListener('dragover', handleDragOver);
        container.addEventListener('drop', handleDrop);
    });

    document.addEventListener('dragstart', handleDragStart);
    document.addEventListener('dragend', handleDragEnd);
}

function handleDragStart(e) {
    if (e.target.classList.contains('ticket')) {
        e.target.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', e.target.dataset.ticketId);
    }
}

function handleDragEnd(e) {
    if (e.target.classList.contains('ticket')) {
        e.target.classList.remove('dragging');
    }
}

function handleDragOver(e) {
    if (e.preventDefault) {
        e.preventDefault();
    }
    e.dataTransfer.dropEffect = 'move';
    return false;
}

async function handleDrop(e) {
    if (e.stopPropagation) {
        e.stopPropagation();
    }
    e.preventDefault();

    const ticketId = e.dataTransfer.getData('text/plain');
    const column = e.currentTarget.closest('.column');
    const newStatus = column.dataset.status;

    try {
        const response = await fetch(`${API_BASE}/tickets/${ticketId}/status`, {
            method: 'PATCH',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ status: newStatus })
        });

        if (response.ok) {
            const updatedTicket = await response.json();
            updateTicketInUI(updatedTicket);
        }
    } catch (error) {
        console.error('Error updating ticket status:', error);
    }

    return false;
}

// Setup event listeners
function setupEventListeners() {
    // New ticket button
    document.getElementById('newTicketBtn').addEventListener('click', () => {
        document.getElementById('newTicketModal').classList.add('active');
    });

    // Settings button
    document.getElementById('settingsBtn').addEventListener('click', showSettings);

    // New ticket form
    document.getElementById('newTicketForm').addEventListener('submit', async (e) => {
        e.preventDefault();
        
        const title = document.getElementById('ticketTitle').value;
        const description = document.getElementById('ticketDescription').value;

        try {
            const response = await fetch(`${API_BASE}/tickets`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    title,
                    description,
                    status: 'Backlog',
                    tasks: [],
                    activityLog: []
                })
            });

            if (response.ok) {
                document.getElementById('newTicketModal').classList.remove('active');
                document.getElementById('newTicketForm').reset();
            }
        } catch (error) {
            console.error('Error creating ticket:', error);
        }
    });

    // Close modals
    document.querySelectorAll('.close').forEach(closeBtn => {
        closeBtn.addEventListener('click', () => {
            closeBtn.closest('.modal').classList.remove('active');
        });
    });

    // Close modals on outside click
    window.addEventListener('click', (e) => {
        if (e.target.classList.contains('modal')) {
            e.target.classList.remove('active');
        }
    });
}

// Show settings
function showSettings() {
    const modal = document.getElementById('settingsModal');
    modal.classList.add('active');

    // Populate git config
    if (settings) {
        document.getElementById('gitUrl').value = settings.gitConfig?.repositoryUrl || '';
        document.getElementById('gitUsername').value = settings.gitConfig?.username || '';
        document.getElementById('gitEmail').value = settings.gitConfig?.email || '';
        document.getElementById('gitSshKey').value = settings.gitConfig?.sshKey || '';
        renderSystemPrompts(settings.systemPrompts || []);
    }

    // Setup git config form
    document.getElementById('gitConfigForm').onsubmit = async (e) => {
        e.preventDefault();
        
        const updatedSettings = {
            ...settings,
            systemPrompts: collectSystemPrompts(settings.systemPrompts || []),
            gitConfig: {
                repositoryUrl: document.getElementById('gitUrl').value,
                username: document.getElementById('gitUsername').value,
                email: document.getElementById('gitEmail').value,
                sshKey: document.getElementById('gitSshKey').value
            }
        };

        try {
            const response = await fetch(`${API_BASE}/settings`, {
                method: 'PUT',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(updatedSettings)
            });

            if (response.ok) {
                settings = await response.json();
                alert('Settings saved successfully!');
            }
        } catch (error) {
            console.error('Error saving settings:', error);
        }
    };
}

function renderSystemPrompts(prompts) {
    const container = document.getElementById('systemPrompts');
    container.innerHTML = '';

    prompts.forEach(prompt => {
        const group = document.createElement('div');
        group.className = 'form-group';

        const label = document.createElement('label');
        label.textContent = prompt.displayName || prompt.key;

        const textarea = document.createElement('textarea');
        textarea.rows = 4;
        textarea.value = prompt.content || '';
        textarea.dataset.promptKey = prompt.key;

        group.appendChild(label);
        group.appendChild(textarea);
        container.appendChild(group);
    });
}

function collectSystemPrompts(existingPrompts) {
    return existingPrompts.map(prompt => {
        const textarea = document.querySelector(`[data-prompt-key="${prompt.key}"]`);
        return {
            ...prompt,
            content: textarea ? textarea.value : prompt.content
        };
    });
}

// Utility function to escape HTML
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Initialize on page load
document.addEventListener('DOMContentLoaded', init);
