# Python Development Guidelines

## Overview
This skill provides guidelines and best practices for Python development within the KanBeast project.

## Python Version
- Use Python 3.12 or higher
- All scripts should include the shebang line: `#!/usr/bin/env python3`

## Project Structure

```
project/
├── src/
│   ├── module1/
│   │   ├── __init__.py
│   │   └── module1.py
│   ├── module2/
│   │   ├── __init__.py
│   │   └── module2.py
│   └── __init__.py
├── tests/
│   ├── test_module1.py
│   └── test_module2.py
├── requirements.txt
└── setup.py
```

## Coding Conventions

### Style
- Follow PEP 8 style guidelines
- Use 4 spaces for indentation (no tabs)
- Maximum line length: 88 characters (Black default)
- Use type hints for function signatures and variables

### Naming
- Variables and functions: `snake_case`
- Classes: `PascalCase`
- Constants: `UPPER_SNAKE_CASE`
- Private members: prefix with `_`
- Dunder methods: `__double_underscore__`

### Imports
- Standard library imports first
- Third-party imports second
- Local imports third
- Use absolute imports when possible
- Avoid wildcard imports (`from module import *`)

## Testing

### Framework
- Use `pytest` for unit testing
- Use `pytest-cov` for coverage reporting
- Use `pytest-mock` for mocking dependencies

### Conventions
- Test files should be named `test_*.py` or `*_test.py`
- Test classes should start with `Test`
- Test methods should start with `test_`
- Use descriptive test names that explain what is being tested

### Coverage
- Aim for at least 80% code coverage
- Focus on covering critical business logic
- Use coverage reports to identify untested code paths

## Package Management

### Requirements
- Use `requirements.txt` for production dependencies
- Use `requirements-dev.txt` for development dependencies
- Pin specific versions for reproducibility

### Installation
```bash
# Install production dependencies
pip install -r requirements.txt

# Install development dependencies
pip install -r requirements-dev.txt
```

## Documentation

### Docstrings
- Use Google-style docstrings
- Include parameter types and return types
- Provide examples for complex functions

### README
- Include project description and purpose
- Document installation and setup instructions
- Include usage examples
- Note any special requirements or considerations

## Error Handling

### Exceptions
- Use specific exception types
- Include meaningful error messages
- Log errors appropriately
- Handle exceptions at the appropriate level

### Logging
- Use the `logging` module
- Configure appropriate log levels
- Include contextual information in log messages

## Security

### Best Practices
- Validate all input data
- Use parameterized queries for database operations
- Avoid hardcoding secrets or credentials
- Use environment variables for configuration

### Dependencies
- Keep dependencies updated
- Regularly scan for vulnerabilities
- Remove unused dependencies

## Performance

### Optimization
- Profile code before optimizing
- Use appropriate data structures
- Consider memory usage
- Avoid premature optimization

### Monitoring
- Include performance metrics where appropriate
- Monitor resource usage
- Set up alerts for performance degradation

## Git Workflow

### Commit Messages
- Use conventional commit format
- Keep messages concise but descriptive
- Reference issue numbers when applicable

### Branching
- Use feature branches for new development
- Keep branches focused and short-lived
- Delete merged branches

## Tools and Configuration

### Linters
- Use `flake8` for style checking
- Use `mypy` for type checking
- Use `black` for code formatting

### Configuration Files
- `.flake8` - flake8 configuration
- `pyproject.toml` - project configuration
- `setup.cfg` - setup configuration

## Installation Instructions

### Python 3.12
```bash
# On Ubuntu/Debian
sudo apt-get update
sudo apt-get install python3.12

# Or using pyenv
pyenv install 3.12.0
pyenv global 3.12.0
```

### pip
```bash
# Upgrade pip
sudo apt-get install python3-pip
sudo pip3 install --upgrade pip

# Or install pip manually
curl https://bootstrap.pypa.io/get-pip.py -o get-pip.py
sudo python3 get-pip.py
```

### Virtual Environments
```bash
# Create virtual environment
python3 -m venv venv

# Activate virtual environment
source venv/bin/activate

# Deactivate virtual environment
deactivate
```

## Common Patterns

### Context Managers
```python
with open('file.txt', 'r') as f:
    content = f.read()
```

### List Comprehensions
```python
squares = [x**2 for x in range(10)]
```

### Exception Handling
```python
try:
    result = risky_operation()
except SpecificError as e:
    handle_error(e)
else:
    process_result(result)
finally:
    cleanup()
```

## Resources

- [Python Documentation](https://docs.python.org/3/)
- [PEP 8 Style Guide](https://peps.python.org/pep-0008/)
- [pytest Documentation](https://docs.pytest.org/)
- [Black Code Formatter](https://black.readthedocs.io/)
- [mypy Type Checker](https://mypy.readthedocs.io/)