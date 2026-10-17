// Colours resolve to CSS variables (see Styles/tailwind.css) so one set of utility classes
// renders GitHub Primer's light or dark palette depending on the `dark` class on <html>.
const token = (name) => `rgb(var(--${name}) / <alpha-value>)`;
const scale = (hue, steps) => Object.fromEntries(steps.map((s) => [s, token(`${hue}-${s}`)]));

module.exports = {
  content: ["./**/*.razor", "./wwwroot/index.html"],
  darkMode: "class",
  theme: {
    extend: {
      colors: {
        gray: scale("gray", [100, 200, 300, 400, 500, 600, 700, 800, 900, 950]),
        red: scale("red", [300, 400, 600, 900]),
        yellow: scale("yellow", [300, 400, 500, 600, 900]),
        green: scale("green", [400]),
        blue: scale("blue", [400, 500]),
        cyan: scale("cyan", [400, 500]),
        violet: scale("violet", [400]),
        sky: scale("sky", [400]),
        coral: token("coral"),
      },
      fontFamily: {
        sans: ['-apple-system', 'BlinkMacSystemFont', '"Segoe UI"', '"Noto Sans"', 'Helvetica', 'Arial', 'sans-serif', '"Apple Color Emoji"', '"Segoe UI Emoji"'],
        mono: ['ui-monospace', 'SFMono-Regular', '"SF Mono"', 'Menlo', 'Consolas', '"Liberation Mono"', 'monospace'],
      },
    },
  },
};
