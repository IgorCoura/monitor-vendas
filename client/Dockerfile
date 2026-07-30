FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .

# Só para o caso de o navegador precisar chamar outro domínio: fica gravado no
# bundle. No arranjo normal (nginx encaminhando /api) não se mexe nisto.
ARG VITE_API_BASE_URL
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL

RUN npm run build

FROM nginx:alpine

# Para onde o nginx encaminha /api. Trocável por variável de ambiente do
# container, sem rebuild — é o ajuste necessário quando front e API são serviços
# separados e o nome do container não é previsível. Não termine com barra.
ENV API_URL=http://api:8080
ENV NGINX_ENVSUBST_FILTER=^API_URL$

COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 3000
