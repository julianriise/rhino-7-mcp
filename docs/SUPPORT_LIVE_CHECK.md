# Support report live checks

Rhino was not opened for these slices. They are local commits on `grok` and are not pushed. The card posts to `https://forsk.app/api/support`. Nothing in these slices was sent there.

The card keeps Type, one description, Email, and Attach debug report. There is no file name field. Labels stay English. The receipt follows the description, or the last line in the thread when the description is empty.

## S1 — a bug from the card

Write a bug, or pick Support so the report card follows.

- The first Send with an empty Email stays on the card. The note is "Type the email we should reply to." A Norwegian description says "Skriv e-postadressen vi skal svare til."
- Type a description and an address you can check. Send. The step line is "Sending…", then one receipt: "Sent. Reference FS-XXXXXXXX — we'll reply by email." The closed card says "Sent". The chat has no JSON.
- A Norwegian description uses "Sender…", then "Sendt. Referanse FS-XXXXXXXX — vi svarer på e-post." The closed card says "Sendt".
- The same address is filled in on the next card, from `~/.forsk/reply-email`. It is not a string in the .3dm.
- An empty description stays on the card: "Write what happened, then send."

## S2 — the mail

A 200 means the report is stored. The mail is sent only when Resend is configured, after that 200.

Look in support@forsk.app.

- The subject is `[Forsk bug]` plus the first line of the description.
- Reply-To is the address from the card. From is Forsk Support.
- The message is the description. Type is `bug`. Plugin, Rhino, and OS are filled when the plugin could read them.
- With the debug box ticked, the mail has `debug-report.txt`. Unticked, the debug line says none, and no attachment.

## S3 — offline, and the outbox

Turn the network off. Send a report with a valid email.

- After the step line, the receipt is "The report didn't go through — it's saved and will be sent later." The closed card says "Saved". Norwegian: "Rapporten kom ikke frem — den er lagret og sendes senere." and "Lagret".
- A file is in `~/.forsk/outbox`. Quit and reopen, or Send another report. The oldest file goes first. A file the server accepts is deleted and is not sent again.
- A restart adds no chat line. The next Send's receipt is for the report you just sent, unless an older file is still failing. Then that receipt is the older failure, and the new report stays in the outbox behind it.
- A file older than 7 days is deleted on that pass and is not sent.
- If Rhino quits after the server has accepted the report and before that file is deleted, the next launch sends it once more. A second mail can arrive.

## S4 — storage unavailable

When the database cannot take the report, the server answers 503 `storage_unavailable`.

- The receipt is "Couldn't reach Forsk support right now — your report is saved and will be sent later." Norwegian: "Fikk ikke kontakt med Forsk support akkurat nå — rapporten er lagret og sendes senere."
- The report stays in `~/.forsk/outbox`. The next Send, or the next time Rhino opens, posts the queue oldest first. A 200 `{ok:true, id}` deletes that file. It is not sent again.
- A report saved earlier, including one kept when the server used to answer `email_not_configured`, goes out on that same pass.
- Too many reports answers "Too many reports just now — try again in N minutes." That one stays queued, and the rest of the queue waits.

## S5 — this list

The checks above are the live pass. Headless gates for these commits are `dotnet build plugin/rhinomcp.csproj` with 0 warnings and `dotnet test tests/SoftParam.Tests`.
