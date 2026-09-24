import { createRoot } from "react-dom/client";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import type { ModelCatalogModelsResponse, ModelCatalogProvidersResponse } from "#neoastra";

const providerReads: Array<{ epoch: string; resolve: (response: ModelCatalogProvidersResponse) => void; reject: (error: Error) => void }> = [];
const modelReads: Array<{ epoch: string; providerId: string; resolve: (response: ModelCatalogModelsResponse) => void; reject: (error: Error) => void }> = [];
const root = createRoot(document.getElementById("app")!);
const readProviders: React.ComponentProps<typeof ModelCatalogPanel>["readProviders"] = request => new Promise((resolve, reject) => {
  providerReads.push({ epoch: request.expectedEpoch, resolve, reject });
});
const readModels: React.ComponentProps<typeof ModelCatalogPanel>["readModels"] = request => new Promise((resolve, reject) => {
  modelReads.push({ epoch: request.expectedEpoch, providerId: request.providerId, resolve, reject });
});
const fixture = {
  providerReads, modelReads,
  show: (epoch: string | null) => root.render(<ModelCatalogPanel epoch={epoch} readProviders={readProviders} readModels={readModels} />),
};
Object.assign(window, { catalogFixture: fixture });
fixture.show("epoch-1");
