import { AxiosRequestConfig } from 'axios';
import APIClient from './api-client';
import { sample_IndexImportance1 } from '../sampleData/sample-indexImportance1';
import { IndexImportance } from '../entities/IndexImportance';

const url = 'data';
class indexImportanceApi extends APIClient<IndexImportance> {
  constructor() {
    super(url);
  }
  getAll = (config: AxiosRequestConfig) => {
    return Promise.resolve({ count: 2, next: null, results: sample_IndexImportance1 });
  };
}
export default indexImportanceApi;
