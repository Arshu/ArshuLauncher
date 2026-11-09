# Recipes: prompts to copy

Start a session (`./runJointLauncher --harness claude`), then paste one of these. The assistant works in the shared
browser while you watch, and you can click in the window yourself at any time. Replace the example address with your own.

## Check a page

- *Open https://example.com and tell me what is on the page.*
- *Open my home page and list every link that does not go anywhere.*
- *Open https://example.com/pricing and check that the three plan cards and their buttons are all visible.*

## Walk through a flow

- *Walk me through the sign-up on my site, one step at a time, and wait for me after each step.*
- *Go through the checkout with a test product. Stop and tell me at the first thing that looks wrong.*
- *Try the password reset flow and tell me what message each step shows.*

## Test a form

- *Fill the contact form with test data and check that the thank-you message appears.*
- *Submit the sign-up form empty, then with a bad email, and tell me what error each one gives.*

## Review together

- *Review the checkout page with me and list anything that looks broken.*
- *Review the page on this address with me. Keep a list of what we find, and ask before you change any code.*
- *We agreed on the list. Fix the first item, rebuild, and run the same steps again.*

## Tips

- Ask for **one step at a time** when you want to watch; ask for the **whole flow** when you want a result.
- Every click is checked: a button that does nothing, a page error or a pop-up is reported, not skipped. Ask the
  assistant what it checked after a step.
- Use `./runJointLauncher --doctor` if the browser does not open, and see [USAGE.md](USAGE.md#if-something-goes-wrong).

More about the features: [FEATURE.md](FEATURE.md).
