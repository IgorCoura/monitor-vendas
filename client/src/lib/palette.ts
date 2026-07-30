// Paleta categórica rosa talco — ordem FIXA, nunca ciclada (regra dataviz).
// Validada com scripts/validate_palette.js sobre a superfície #FAF3F1:
// lightness band, chroma floor, CVD ΔE >= 8, normal-vision >= 15, contraste 3:1.
export const chartPalette = [
  '#C25E77', // 1. rosa empoeirado (série principal)
  '#4C86C6', // 2. azul
  '#C67947', // 3. terracota
  '#8E6BAE', // 4. malva
  '#4E9D57', // 5. verde
] as const

export const chartSeries = {
  primary: chartPalette[0],
  secondary: chartPalette[1],
}

// Tokens de texto/estrutura dos gráficos — texto usa tinta, nunca cor de série.
export const chartInk = {
  axis: '#8A7379',
  grid: '#F0DEDA',
  tooltipBg: '#FFFFFF',
}
