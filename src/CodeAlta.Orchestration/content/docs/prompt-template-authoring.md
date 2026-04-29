# Prompt Template Authoring

A prompt template is an optional `template.yml` file that selects base/role names and toggles generated prompt parts.

```yaml
version: 1
base: default
role: default
skills: true
project_context: true
runtime_context: true
tool_guidance: true
```

The file is not a resource index and should not list paths.