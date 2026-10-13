---
title: Speech to Prompt
---

# Speech to prompt

In CodeAlta Desktop you can dictate a prompt instead of typing it. A speech model that runs on your computer writes what you say at the caret of the prompt. Your voice stays on your computer: it is not sent to a model provider or to any other service.

## Dictate a prompt

Click the **microphone** button beside **Send**, or press `F9`, and speak. The button turns red and follows your voice while CodeAlta listens.

- What is being recognized shows in grey at the caret. It can still change while you speak.
- Each time you pause, the sentence is written in the prompt. It is one edit: `Ctrl+Z` removes it.
- Click the button again, or press `F9` or `Esc`, to stop: the words you were saying are written too.
- Sending the prompt stops the dictation. A sentence you had not finished is not added to the prompt that was sent.

What you dictated is ordinary text of the prompt: correct it, add to it, then send it as usual. Names of files, functions and commands are often misheard, so read them before you send.

The microphone button is in the prompt of a session and in the prompt of a new session.

## The first use downloads a model

Nothing is downloaded before you ask for it. The first click on the microphone shows the model and its size, with **Download** and **Cancel**, and a link to choose another model.

A notification then shows the download and the installation, with a **Cancel** button. You can keep working, and the download goes on when you close Settings. A download that was canceled or interrupted goes on from where it stopped the next time.

The first download has two parts:

- the speech model, from [Hugging Face](https://huggingface.co/);
- the speech engine ([sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), about 10 MiB), from nuget.org.

Every file is checked against its checksum before it is used.

## Choose the model

**Settings > Personalization > Speech** turns speech to prompt on or off, chooses the model, and lists the models you downloaded with their size and a button to delete each of them.

| Model | Languages | Download | From |
| --- | --- | --- | --- |
| **Parakeet TDT 0.6b v3** (the default) | 25 European languages, recognized by themselves | 639 MiB | NVIDIA, CC BY 4.0 |
| Parakeet TDT 0.6b v2 | English | 631 MiB | NVIDIA, CC BY 4.0 |
| Whisper small | 99 languages | 358 MiB | OpenAI, MIT |
| Moonshine base | English | 135 MiB | Moonshine AI, MIT |
| Moonshine tiny | English | 42 MiB | Moonshine AI, MIT |

The Parakeet models write punctuation and capital letters, and are the most accurate of the list. The Moonshine models are much smaller, for a quick download or a slow computer.

### A model of your own

Choose **Custom** and give the address of a Hugging Face repository, for example `https://huggingface.co/<owner>/<repository>`, or the address of one of its folders (`.../tree/<revision>/<folder>`). CodeAlta reads the list of its files and recognizes these models of [sherpa-onnx](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/index.html):

- a transducer: `encoder*.onnx`, `decoder*.onnx`, `joiner*.onnx` and `tokens.txt`;
- Moonshine: `encoder_model.*`, `decoder_model_merged.*` and `tokens.txt`, or the four files of its first version;
- Whisper: `<name>-encoder*.onnx`, `<name>-decoder*.onnx` and `<name>-tokens.txt`.

When a repository has both, the quantized (`int8`) files are downloaded. A repository that holds none of these is refused before anything is downloaded. A model is loaded once after its download: one that the engine cannot load is reported then, and can be deleted on the same page.

## Where the models are

Models and the engine are in `~/.alta/cache/speech`. The folder can be deleted: it is downloaded again at the next use.

The model stays in memory for a few minutes after a dictation, so that the next one starts at once, then the memory is given back.

The choices are in the `[speech]` table of `~/.alta/config.toml`. The defaults are not written:

```toml
[speech]
enabled = false          # true when absent
model = "whisper-small"  # a model of the table above, or the address of a repository
```

| Model | `model` |
| --- | --- |
| Parakeet TDT 0.6b v3 | `parakeet-tdt-0.6b-v3` |
| Parakeet TDT 0.6b v2 | `parakeet-tdt-0.6b-v2` |
| Whisper small | `whisper-small` |
| Moonshine base | `moonshine-base-en` |
| Moonshine tiny | `moonshine-tiny-en` |

## The microphone

The first time, the system may ask whether CodeAlta can use the microphone.

- **Windows**: in **Settings > Privacy & security > Microphone**, **Let desktop apps access your microphone** must be on.
- **macOS**: allow **CodeAlta** in **System Settings > Privacy & Security > Microphone**. Started from a terminal, CodeAlta uses the permission of that terminal.
- **Linux**: speech to prompt is not available yet, and the microphone button is not shown.

When speech to prompt is turned off, CodeAlta does not ask for the microphone and the button is not shown.
